using System;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using BancoBr.API.Base;
using BancoBr.Common.Enums;
using BancoBr.Common.Instances;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BancoBr.API.Itau
{
    /// <summary>Inclusão direta Pix SISPAG, sem resolução DICT ou confirmação em chamada separada.</summary>
    internal sealed class PagamentoPixClient : PagamentoPixApiBase, IPagamentoDiretoApi
    {
        private readonly ItauHttpGateway _gateway;
        // Não altera o domínio: preserva corpo e identidade nas tentativas do mesmo objeto.
        private readonly ConditionalWeakTable<Movimento, Tuple<string, JObject>> _tentativas = new ConditionalWeakTable<Movimento, Tuple<string, JObject>>();

        internal PagamentoPixClient(ItauHttpGateway gateway, Correntista origem) : base(null, origem) { _gateway = gateway; }

        public async Task<Movimento> EnviarAsync(Movimento movimento, string idempotencyKey, CancellationToken cancellationToken)
        {
            if (!(movimento.MovimentoItem is MovimentoItemTransferenciaPIX) && !(movimento.MovimentoItem is MovimentoItemPagamentoTituloPIXQRCode))
                throw new NotSupportedException("A API SISPAG Itaú permite incluir somente Pix nesta integração.");
            if (!string.IsNullOrEmpty(movimento.NumeroDocumentoNoBanco))
                return await ConsultarPagamentoAsync(movimento, cancellationToken).ConfigureAwait(false);
            var tentativa = _tentativas.GetValue(movimento, m =>
            {
                // O consumidor deve persistir a chave para retentativas após reiniciar a aplicação.
                var chave = idempotencyKey ?? m.NumeroDocumento;
                if (string.IsNullOrWhiteSpace(chave) || chave.Length > 255 || chave.Any(c => c < 32 || c > 126))
                    throw new ArgumentException("Informe idempotencyKey ou NumeroDocumento estável, com até 255 caracteres ASCII imprimíveis.");
                return Tuple.Create(chave, CriarPagamento(m));
            });
            if (!JToken.DeepEquals(tentativa.Item2, CriarPagamento(movimento)))
                throw new InvalidOperationException("O pagamento Itaú foi alterado após uma tentativa. Preserve os dados originais e a mesma identidade no reenvio.");
            var resposta = await _gateway.JsonAsync(HttpMethod.Post, "transferencias", tentativa.Item2, cancellationToken, tentativa.Item1).ConfigureAwait(false);
            var status = (string)resposta["status_pagamento"];
            if (status == "Rejeitado" || status == "Nao incluido")
            {
                movimento.SituacaoBancoBr = BancoBrSituacaoEnum.Rejeitado;
                var erro = ItauHttpGateway.Erro(resposta, 200, status);
                movimento.DetalheRejeicaoBancoBr = erro.Message;
                throw erro;
            }
            if (status != "Sucesso" && status != "Sucesso (pre-autorizado)")
                throw new JsonSerializationException("Status de inclusão Itaú desconhecido.");
            var id = (string)resposta["cod_pagamento"];
            if (!Guid.TryParse(id, out _)) throw new JsonSerializationException("Inclusão Itaú sem cod_pagamento válido.");
            movimento.NumeroDocumentoNoBanco = id;
            // Sucesso na inclusão não confirma liquidação nem dispensa alçada no SISPAG.
            movimento.SituacaoBancoBr = BancoBrSituacaoEnum.Agendado;
            movimento.DetalheRejeicaoBancoBr = null;
            return movimento;
        }

        private JObject CriarPagamento(Movimento m)
        {
            if (m.DataPagamento == default || m.ValorPagamento <= 0 || decimal.Round(m.ValorPagamento, 2) != m.ValorPagamento || m.ValorPagamento >= 1000000000000000m)
                throw new ArgumentException("Informe data e valor positivo com até duas casas decimais para o Pix Itaú.");
            if (m.Moeda != "BRL") throw new NotSupportedException("Pix Itaú exige BRL.");
            var documento = Documento(Origem.CPF_CNPJ, Origem.TipoPessoa);
            var body = new JObject
            {
                ["valor_pagamento"] = m.ValorPagamento,
                ["data_pagamento"] = m.DataPagamento.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["pagador"] = new JObject
                {
                    ["tipo_conta"] = "CC", ["agencia"] = Agencia(Origem.NumeroAgencia),
                    ["conta"] = Conta(Origem.NumeroConta, Origem.DVConta, 8),
                    ["tipo_pessoa"] = Pessoa(Origem.TipoPessoa), ["documento"] = documento,
                    ["modulo_sispag"] = "Fornecedores"
                }
            };
            if (!string.IsNullOrEmpty(m.NumeroDocumento))
            {
                if (m.NumeroDocumento.Length > 20) throw new ArgumentException("NumeroDocumento excede as 20 posições da referencia_empresa Itaú.");
                body["referencia_empresa"] = m.NumeroDocumento;
            }
            if (m.MovimentoItem is MovimentoItemTransferenciaPIX pix)
            {
                if (!string.IsNullOrWhiteSpace(pix.ChavePIX))
                {
                    if (pix.ChavePIX.Length > 76) throw new ArgumentException("Chave Pix excede 76 posições.");
                    body["chave"] = pix.ChavePIX;
                }
                else
                {
                    if (pix.Ispb == null || pix.Ispb.Length != 8 || pix.Ispb.Any(c => c < '0' || c > '9'))
                        throw new ArgumentException("Informe Ispb com oito dígitos para Pix Itaú por dados bancários.");
                    if (m.Favorecido == null) throw new ArgumentException("Informe o favorecido do Pix por dados bancários.");
                    body["ispb"] = pix.Ispb;
                    body["tipo_identificacao_conta"] = TipoConta(pix.TipoConta);
                    body["agencia_recebedor"] = Agencia(pix.NumeroAgencia);
                    body["conta_recebedor"] = Conta(pix.NumeroConta, pix.DVConta, 20);
                    body["tipo_de_identificacao_do_recebedor"] = Pessoa(m.Favorecido.TipoPessoa);
                    body["identificacao_recebedor"] = Documento(m.Favorecido.CPF_CNPJ, m.Favorecido.TipoPessoa);
                }
            }
            else
            {
                var qr = (MovimentoItemPagamentoTituloPIXQRCode)m.MovimentoItem;
                if (qr.PagarComJurosMulta) throw new NotSupportedException("Informe o valor final no Pix Itaú; não há autorização de juros automática neste contrato.");
                if (!string.IsNullOrWhiteSpace(qr.QrCode))
                {
                    if (qr.QrCode.Length > 1800) throw new ArgumentException("QrCode EMV excede 1800 posições.");
                    body["emv"] = qr.QrCode;
                }
                else
                {
                    // URL do QR Code é uma alternativa ao EMV no mesmo POST /transferencias.
                    var url = qr.URL_ChavePix;
                    var completa = url != null && url.IndexOf("://", StringComparison.Ordinal) < 0 ? "https://" + url : url;
                    if (string.IsNullOrWhiteSpace(url) || url != url.Trim() || url.Length > 77 || !Uri.TryCreate(completa, UriKind.Absolute, out var uri) ||
                        uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
                        throw new ArgumentException("Informe QrCode EMV ou URL_ChavePix HTTPS com até 77 posições.");
                    body["url"] = url;
                }
                if (!string.IsNullOrEmpty(qr.TXID))
                {
                    if (qr.TXID.Length > 35) throw new ArgumentException("TXID excede 35 posições.");
                    body["txid"] = qr.TXID;
                }
            }
            return body;
        }

        internal override async Task<Movimento> ConsultarPagamentoAsync(Movimento m, CancellationToken cancellationToken = default)
        {
            if (!Guid.TryParse(m.NumeroDocumentoNoBanco, out _)) throw new ArgumentException("Informe o id do pagamento Itaú em NumeroDocumentoNoBanco.");
            var r = await _gateway.JsonAsync(HttpMethod.Get, "pagamentos_sispag/" + Uri.EscapeDataString(m.NumeroDocumentoNoBanco), null, cancellationToken).ConfigureAwait(false);
            // Schema direto e envelopes demonstrados nos exemplos oficiais e no guia adicional.
            var dados = r["dados_pagamento"] ?? r["data"]?["dados_pagamento"] ?? r["value"]?["data"]?["dados_pagamento"];
            if (!(dados is JObject) || !string.Equals((string)dados["id_pagamento"], m.NumeroDocumentoNoBanco, StringComparison.OrdinalIgnoreCase))
                throw new JsonSerializationException("Detalhe Itaú ausente ou de outro pagamento.");
            var debito = r["dados_debito"] ?? r["data"]?["dados_debito"] ?? r["value"]?["data"]?["dados_debito"];
            if (debito == null || ((string)debito["numero_agencia_debito"])?.PadLeft(4, '0') != Agencia(Origem.NumeroAgencia) ||
                ((string)debito["numero_conta_debito"])?.PadLeft(8, '0') != Conta(Origem.NumeroConta, Origem.DVConta, 8))
                throw new JsonSerializationException("Detalhe Itaú não corresponde à conta configurada.");
            AplicarDetalhe(m, dados, debito);
            var status = (string)dados["status"];
            switch (status)
            {
                case "EF": case "Efetuado": case "Efetuados": m.SituacaoBancoBr = BancoBrSituacaoEnum.Efetivado; break;
                case "AE": case "A Efetuar": case "A efetuar": m.SituacaoBancoBr = BancoBrSituacaoEnum.Agendado; break;
                case "NE": case "Não Efetuado": case "Não efetuado": case "Não Efetuados": m.SituacaoBancoBr = BancoBrSituacaoEnum.Rejeitado; break;
                default: m.SituacaoBancoBr = BancoBrSituacaoEnum.NaoIntegrado; break;
            }
            var motivos = dados["motivo_rejeicao"] as JArray;
            m.DetalheRejeicaoBancoBr = motivos == null || motivos.Count == 0 ? null : string.Join("; ", motivos.Select(x =>
                x.Type == JTokenType.String ? (string)x : (string)x["codigo_motivo"] + ": " + (string)x["descricao_motivo"]));
            return m;
        }

        private static void AplicarDetalhe(Movimento m, JToken dados, JToken debito)
        {
            var valor = DecimalOpcional(dados["valor_pagamento"]);
            var data = DataOpcional(dados["data_pagamento"]);
            var boleto = dados["dados_boleto"];
            var qr = dados["dados_pix_qr_code"];
            var vencimentoBoleto = DataOpcional(boleto?["data_vencimento"]);
            var valorBoleto = DecimalOpcional(boleto?["valor_boleto"]);
            var descontoBoleto = DecimalOpcional(boleto?["valor_descontos"]);
            var vencimentoQr = DataOpcional(qr?["data_vencimento"]);
            var valorQr = DecimalOpcional(qr?["valor_documento"]);
            var multaQr = DecimalOpcional(qr?["multa"]);
            var jurosQr = DecimalOpcional(qr?["juros"]);
            var descontoQr = DecimalOpcional(qr?["desconto"]);

            // Converte os campos numéricos e datas antes de alterar o movimento do consumidor.
            if (valor.HasValue) m.ValorPagamento = valor.Value;
            if (data.HasValue) m.DataPagamento = data.Value;
            if (dados["referencia_empresa"]?.Type == JTokenType.String) m.NumeroDocumento = (string)dados["referencia_empresa"];
            var nome = (string)dados["nome_favorecido"];
            var documento = (string)dados["cpf_cnpj_favorecido"];
            if (nome != null || documento != null)
            {
                if (m.Favorecido == null) m.Favorecido = new Favorecido();
                if (nome != null) m.Favorecido.Nome = nome;
                if (documento != null) m.Favorecido.CPF_CNPJ = documento;
                if ((string)dados["cod_tipo_pessoa"] == "F") m.Favorecido.TipoPessoa = TipoInscricaoCPFCNPJEnum.CPF;
                if ((string)dados["cod_tipo_pessoa"] == "J") m.Favorecido.TipoPessoa = TipoInscricaoCPFCNPJEnum.CNPJ;
            }
            if (m.MovimentoItem is MovimentoItemTransferenciaPIX pix)
            {
                if (nome != null) pix.TitularConfirmadoNome = nome;
                if (documento != null) pix.TitularConfirmadoDocumento = documento;
                if (dados["cod_tipo_pessoa"] != null) pix.TitularConfirmadoTipo = (string)dados["cod_tipo_pessoa"];
                // A chave originalmente enviada é preservada; a consulta pode mascará-la.
            }
            if (m.MovimentoItem is MovimentoItemPagamentoTituloCodigoBarra itemBoleto && boleto is JObject)
            {
                if (boleto["linha_digitavel"] != null) itemBoleto.NumeroLinhaDigitavel = (string)boleto["linha_digitavel"];
                if (vencimentoBoleto.HasValue) itemBoleto.DataVencimento = vencimentoBoleto;
                if (valorBoleto.HasValue) itemBoleto.ValorCodigoBarra = valorBoleto.Value;
                if (descontoBoleto.HasValue) itemBoleto.Desconto = descontoBoleto.Value;
                if (boleto["nome_pagador"] != null) itemBoleto.PagadorConfirmadoNome = (string)boleto["nome_pagador"];
                if (boleto["documento_pagador"] != null) itemBoleto.PagadorConfirmadoDocumento = (string)boleto["documento_pagador"];
            }
            if (m.MovimentoItem is MovimentoItemPagamentoConvenioCodigoBarra convenio)
            {
                var dadosBarras = dados["dados_concessionaria"] ?? dados["dados_tributos_cod_barras"] ?? dados["dados_das_cod_barras"] ?? dados["dados_fgts"];
                if (dadosBarras is JObject && dadosBarras["linha_digitavel"] != null) convenio.CodigoBarra = (string)dadosBarras["linha_digitavel"];
            }
            if (m.MovimentoItem is MovimentoItemPagamentoTituloPIXQRCode itemQr && qr is JObject)
            {
                if (vencimentoQr.HasValue) itemQr.DataVencimento = vencimentoQr.Value;
                if (valorQr.HasValue) itemQr.ValorOriginal = valorQr;
                if (multaQr.HasValue) itemQr.Multa = multaQr;
                if (jurosQr.HasValue) itemQr.Juros = jurosQr;
                if (descontoQr.HasValue) itemQr.Desconto = descontoQr;
                if (qr["identificador_qr_code"] != null) itemQr.TXID = (string)qr["identificador_qr_code"];
            }
            if (m.MovimentoItem is MovimentoItemTransferenciaTED ted)
            {
                ted.Situacao = (string)dados["status"];
                if (dados["dados_ted"]?["complemento_finalidade_ted"] != null) ted.Historico = (string)dados["dados_ted"]["complemento_finalidade_ted"];
                if (debito["nome_empresa_debito"] != null) ted.NomePessoaDebito = (string)debito["nome_empresa_debito"];
                if (debito["cnpj_debito"] != null) ted.NumeroCPFCNPJDebito = (string)debito["cnpj_debito"];
            }
        }

        private static decimal? DecimalOpcional(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null || (token.Type == JTokenType.String && string.IsNullOrWhiteSpace((string)token))) return null;
            // Há exemplos oficiais com vírgula decimal; não aceitar separadores de milhar.
            var texto = token.Type == JTokenType.String ? (string)token : token.ToString(Formatting.None);
            if (!decimal.TryParse(texto.Replace(',', '.'), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var valor)) throw new JsonSerializationException("Valor monetário Itaú inválido.");
            return valor;
        }

        private static DateTime? DataOpcional(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null || (token.Type == JTokenType.String && string.IsNullOrWhiteSpace((string)token))) return null;
            if (token.Type == JTokenType.Date) return token.Value<DateTime>().Date;
            if (!DateTime.TryParseExact((string)token, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var data))
                throw new JsonSerializationException("Data de pagamento/vencimento Itaú inválida.");
            return data;
        }

        internal static string Agencia(int agencia)
        {
            if (agencia < 1 || agencia > 9999) throw new ArgumentException("Agência Itaú deve ter de um a quatro dígitos.");
            return agencia.ToString("D4", CultureInfo.InvariantCulture);
        }

        internal static string Conta(long conta, string dv, int tamanho)
        {
            if (conta < 0 || string.IsNullOrEmpty(dv) || dv.Length != 1 || dv[0] < '0' || dv[0] > '9')
                throw new ArgumentException("Informe conta e DV numérico.");
            var valor = conta.ToString(CultureInfo.InvariantCulture) + dv;
            if (valor.Length > tamanho) throw new ArgumentException("Conta excede o tamanho aceito pelo Itaú.");
            return tamanho == 8 ? valor.PadLeft(8, '0') : valor;
        }

        private static string Pessoa(TipoInscricaoCPFCNPJEnum tipo) => tipo == TipoInscricaoCPFCNPJEnum.CPF ? "F" :
            tipo == TipoInscricaoCPFCNPJEnum.CNPJ ? "J" : throw new ArgumentException("Informe CPF ou CNPJ.");
        private static string Documento(string valor, TipoInscricaoCPFCNPJEnum tipo)
        {
            Pessoa(tipo);
            var digitos = new string((valor ?? "").Where(c => c >= '0' && c <= '9').ToArray());
            if (digitos.Length != (tipo == TipoInscricaoCPFCNPJEnum.CPF ? 11 : 14)) throw new ArgumentException("Documento com tamanho inválido.");
            return digitos;
        }
        private static string TipoConta(TipoContaEnum tipo)
        {
            switch (tipo)
            {
                case TipoContaEnum.ContaCorrente: return "CC";
                case TipoContaEnum.ContaPoupanca: return "PP";
                case TipoContaEnum.ContaPagamento: return "CP";
                default: throw new ArgumentException("Tipo de conta Pix inválido.");
            }
        }

        public Task<Movimento> CancelarAsync(Movimento m, CancellationToken ct) => throw new NotSupportedException("A referência SISPAG Itaú não expõe cancelamento por API.");
        internal override Task<Movimento> IniciarPagamentoAsync(Movimento m, CancellationToken cancellationToken = default) => throw new NotSupportedException("Itaú não expõe consulta prévia da chave; use EnviarAgendamentoAsync.");
        internal override Task<Movimento> ConfirmarPagamentoAsync(Movimento m, CancellationToken cancellationToken = default) => ConsultarPagamentoAsync(m, cancellationToken);
        internal override Task<Movimento> PagarComIniciacaoAsync(Movimento m, CancellationToken cancellationToken = default) => EnviarAsync(m, null, cancellationToken);
        internal override Task<Movimento> PagarViaQrCodeAsync(Movimento m, CancellationToken cancellationToken = default) => EnviarAsync(m, null, cancellationToken);
    }
}
