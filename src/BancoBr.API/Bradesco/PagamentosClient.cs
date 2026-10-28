using System;
using System.Runtime.CompilerServices;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BancoBr.API.Base;
using BancoBr.Common.Enums;
using BancoBr.Common.Instances;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BancoBr.API.Bradesco
{
    /// <summary>Fluxos de pagamento conforme os manuais públicos Bradesco. Nunca repete uma execução automaticamente.</summary>
    internal sealed class PagamentosClient : IPagamentoDiretoApi
    {
        private static readonly ConditionalWeakTable<Movimento, StrongBox<string>> Identidades = new ConditionalWeakTable<Movimento, StrongBox<string>>();
        private static StrongBox<string> IdentidadeSalva(Movimento movimento) => Identidades.GetValue(movimento, _ => new StrongBox<string>());
        internal BradescoHttpGateway Gateway { get; }
        private readonly BradescoHttpGateway _pix;
        private Correntista Origem => Gateway.Origem;
        private const string Boleto = "boleto/pagamento-cobranca/v1/";
        private const string Convenio = "pagamento/arrecadacao-via-codbarras/v1/";
        internal PagamentosClient(BradescoHttpGateway gateway, BradescoHttpGateway pix) { Gateway = gateway; _pix = pix; }
        private static string Digitos(string s) => BradescoHttpGateway.Digitos(s);
        private static string Data(DateTime data) => data.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        private static int DataNumero(DateTime data) => int.Parse(data.ToString("yyyyMMdd", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

        private string Identidade(Movimento m, string chave, int limite, bool numerica)
        {
            // NumeroDocumento é a identidade bancária explícita para protocolos menores que um UUID.
            var valor = m.NumeroDocumento ?? chave ?? IdentidadeSalva(m).Value;
            if (limite == 35 && Guid.TryParse(valor, out var guid)) valor = guid.ToString("N");
            if (string.IsNullOrWhiteSpace(valor) && !numerica) valor = Guid.NewGuid().ToString("N");
            if (string.IsNullOrWhiteSpace(valor) || valor.Length > limite || (numerica && (!valor.All(c => c >= '0' && c <= '9') || long.Parse(valor, CultureInfo.InvariantCulture) == 0)))
                throw new ArgumentException("Informe NumeroDocumento único e estável, " + (numerica ? "numérico positivo " : "") + "com até " + limite + " caracteres para esta operação Bradesco.");
            var identidadeSalva = IdentidadeSalva(m);
            if (identidadeSalva.Value != null && identidadeSalva.Value != valor)
                throw new ArgumentException("Preserve NumeroDocumento e a identidade bancária entre tentativas.");
            identidadeSalva.Value = valor;
            return valor;
        }

        public async Task<Movimento> EnviarAsync(Movimento m, string idempotencyKey, CancellationToken ct)
        {
            if (m.DataPagamento == default || m.ValorPagamento <= 0 || decimal.Round(m.ValorPagamento, 2) != m.ValorPagamento)
                throw new ArgumentException("Informe DataPagamento e ValorPagamento positivo com até duas casas decimais.");
            switch (m.MovimentoItem)
            {
                case MovimentoItemPagamentoTituloCodigoBarra _:
                    Identidade(m, idempotencyKey, 9, true);
                    await ConsultarBoletoAsync(m, ct).ConfigureAwait(false);
                    return await PagarBoletoAsync(m, ct).ConfigureAwait(false);
                case MovimentoItemPagamentoConvenioCodigoBarra _:
                    if (m.DataPagamento.Date != DateTime.Today) throw new NotSupportedException("Esta integração de arrecadação Bradesco suporta débito no dia; agendamento não homologado.");
                    Identidade(m, idempotencyKey, 9, false);
                    await ConvenioAsync(m, false, ct).ConfigureAwait(false);
                    return await ConvenioAsync(m, true, ct).ConfigureAwait(false);
                case MovimentoItemTransferenciaPIX pix:
                    if (m.DataPagamento.Date != DateTime.Today) throw new NotSupportedException("A API de transferências Pix documentada não oferece agendamento.");
                    var id = Identidade(m, idempotencyKey, 35, false);
                    var favorecido = m.Favorecido ?? throw new ArgumentException("Informe o favorecido do Pix.");
                    var recebedor = new JObject { ["cpfCnpj"] = Documento(favorecido.CPF_CNPJ), ["nomeFavorecido"] = favorecido.Nome };
                    if (!string.IsNullOrWhiteSpace(pix.ChavePIX))
                    {
                        recebedor["tipoChave"] = TipoChave(pix.TipoChavePIX);
                        recebedor["chavePix"] = pix.ChavePIX;
                    }
                    else
                    {
                        if (string.IsNullOrEmpty(pix.Ispb) || pix.Ispb.Length != 8 || pix.NumeroAgencia <= 0 || pix.NumeroConta <= 0)
                            throw new ArgumentException("Pix por conta exige ISPB de oito dígitos, agência e conta.");
                        recebedor["tipoChave"] = "AGENCIACONTA";
                        recebedor["ispb"] = pix.Ispb;
                        if (pix.Banco > 0) recebedor["banco"] = pix.Banco.ToString(CultureInfo.InvariantCulture);
                        recebedor["agencia"] = pix.NumeroAgencia.ToString("D4", CultureInfo.InvariantCulture);
                        recebedor["conta"] = pix.NumeroConta.ToString(CultureInfo.InvariantCulture);
                        recebedor["digitoConta"] = pix.DVConta;
                        recebedor["tipoConta"] = pix.TipoConta == TipoContaEnum.ContaPoupanca ? "CONTA_POUPANCA" : pix.TipoConta == TipoContaEnum.ContaPagamento ? "CONTA_PAGAMENTO" : "CONTA_CORRENTE";
                    }
                    var r = await _pix.JsonAsync(HttpMethod.Post, "v1/spi/solicitar-transferencia", new JObject
                    {
                        ["pagador"] = new JObject { ["tipoChave"] = "AGENCIACONTA", ["agencia"] = Origem.NumeroAgencia.ToString("D4", CultureInfo.InvariantCulture), ["conta"] = Origem.NumeroConta.ToString(CultureInfo.InvariantCulture) },
                        ["recebedor"] = recebedor, ["idtransacao"] = id,
                        ["valor"] = m.ValorPagamento.ToString("F2", CultureInfo.InvariantCulture), ["descricao"] = m.NumeroDocumento ?? "Pagamento"
                    }, ct).ConfigureAwait(false);
                    // A consulta por idtransacao permite recuperar inclusive um envio cujo retorno se perdeu.
                    m.NumeroDocumentoNoBanco = id;
                    AplicarPix(m, r);
                    return m;
                case MovimentoItemTransferenciaTED ted:
                    if (m.DataPagamento.Date != DateTime.Today) throw new NotSupportedException("A API TED documentada oferece somente envio no dia.");
                    var tedId = Identidade(m, idempotencyKey, 25, false);
                    var f = m.Favorecido ?? throw new ArgumentException("Informe o favorecido da TED.");
                    var documento = Documento(f.CPF_CNPJ);
                    var partes = PartesDocumento(documento);
                    var result = await Gateway.JsonAsync(HttpMethod.Post, "transferencia/ted/v1/efetiva", new JObject
                    {
                        ["identificadorDoTipoDeTransferencia"] = 1, ["agenciaRemetente"] = Origem.NumeroAgencia,
                        ["contaRemetenteComDigito"] = ContaComDigito(Origem.NumeroConta, Origem.DVConta),
                        ["tipoContaRemetente"] = "CC", ["tipoDePessoaRemetente"] = Documento(Origem.CPF_CNPJ).Length == 11 ? "F" : "J", ["nomeClienteRemetente"] = Origem.Nome,
                        ["bancoDestinatario"] = ted.Banco, ["agenciaDestinatario"] = ted.NumeroAgencia,
                        ["contaDestinatario"] = ContaComDigito(ted.NumeroConta, ted.DVConta), ["tipoDeContaDestinatario"] = "CC",
                        ["tipodePessoaDestinatario"] = documento.Length == 11 ? "F" : "J", ["nomeClienteDestinatario"] = f.Nome,
                        ["numeroInscricao"] = partes[0], ["numeroFilial"] = partes[1], ["numeroControle"] = partes[2],
                        ["valorDaTransferencia"] = m.ValorPagamento, ["finalidadeDaTransferencia"] = (int)ted.CodigoFinalidadeTED,
                        ["codigoIdentificadorDaTransferencia"] = tedId, ["dataMovimento"] = m.DataPagamento.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture),
                        ["tipoDeDoc"] = m.TipoLancamento == TipoLancamentoEnum.TEDMesmaTitularidade ? "D" : "E"
                    }, ct).ConfigureAwait(false);
                    var bancoId = (string)result["chaveUnicaParaApi"];
                    if (string.IsNullOrWhiteSpace(bancoId) || bancoId.Length < 7) throw new JsonSerializationException("TED sem chaveUnicaParaApi válida; reconcilie antes de reenviar.");
                    m.NumeroDocumentoNoBanco = bancoId;
                    m.SituacaoBancoBr = null;

                    return m;
                default: throw new NotSupportedException("Modalidade não documentada nas APIs públicas Bradesco (incluindo Pix QR Code).");
            }
        }

        internal async Task<Movimento> ConsultarBoletoAsync(Movimento m, CancellationToken ct)
        {
            var item = (MovimentoItemPagamentoTituloCodigoBarra)m.MovimentoItem;
            var codigo = Digitos(item.CodigoBarras);
            if (codigo.Length != 44 && codigo.Length != 47) throw new ArgumentException("Informe código de barras de 44 ou linha digitável de 47 dígitos.");
            var linha = codigo.Length == 47 ? codigo.Substring(0, 5) + "." + codigo.Substring(5, 5) + " " + codigo.Substring(10, 5) + "." + codigo.Substring(15, 6) + " " + codigo.Substring(21, 5) + "." + codigo.Substring(26, 6) + " " + codigo.Substring(32, 1) + " " + codigo.Substring(33) : "0000000000" + codigo;
            var r = await Gateway.JsonAsync(HttpMethod.Post, Boleto + "cobranca-valida-titulo-pagamento/validaTituloPagamento", new JObject
            {
                ["tipoEntrada"] = codigo.Length == 44 ? 1 : 2, ["dadosEntrada"] = linha,
                ["bancoTitulo"] = int.Parse(codigo.Substring(0, 3), CultureInfo.InvariantCulture), ["agenciaDeb"] = Origem.NumeroAgencia, ["contaDeb"] = Origem.NumeroConta
            }, ct).ConfigureAwait(false);
            m.SituacaoBancoBr = null;
            item.CodigoBarras = (string)r["codBarras"] ?? throw new JsonSerializationException("Validação sem codBarras.");
            item.IdentificadorConsulta = (string)r["numeroCtrlCip"];
            if ((string)r["consultaCip"] == "S" && string.IsNullOrEmpty(item.IdentificadorConsulta))
                throw new JsonSerializationException("Validação CIP sem número de controle.");
            item.ValorCodigoBarra = r.Value<decimal>("valorTitulo");
            if (r.Value<int?>("dataVencimento") is int vencimento && vencimento > 0)
                item.DataVencimento = DateTime.ParseExact(vencimento.ToString(CultureInfo.InvariantCulture), "yyyyMMdd", CultureInfo.InvariantCulture);
            m.Favorecido = new Favorecido { Nome = (string)r["nmCedente"], CPF_CNPJ = (string)r["cnpjCpfCedente"] };
            return m;
        }

        private JObject DadosBoleto(Movimento m)
        {
            var item = (MovimentoItemPagamentoTituloCodigoBarra)m.MovimentoItem;
            return new JObject
            {
                ["formaCaptura"] = 1, ["codigoBarras"] = item.CodigoBarras,
                ["dataVencimento"] = DataNumero(item.DataVencimento ?? throw new ArgumentException("Validação sem data de vencimento.")), ["valorTitulo"] = item.ValorCodigoBarra,
                ["dataMovimento"] = DataNumero(DateTime.Today), ["dataPagamento"] = DataNumero(m.DataPagamento),
                ["horaTransacao"] = int.Parse(DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture),
                ["numeroAutenticacao"] = "", ["formaPagamento"] = 2, ["bancoDebito"] = 237,
                ["agenciaDebito"] = Origem.NumeroAgencia, ["digitoAgencia"] = DigitoAgencia(),
                ["contaDebito"] = Origem.NumeroConta, ["digitoConta"] = Origem.DVConta,
                ["linhaExtrato"] = m.NumeroDocumento, ["numeroCtrlCip"] = item.IdentificadorConsulta ?? ""
            };
        }

        internal async Task<Movimento> PagarBoletoAsync(Movimento m, CancellationToken ct)
        {
            var body = DadosBoleto(m);
            body["agencia"] = Origem.NumeroAgencia;
            var previa = await Gateway.JsonAsync(HttpMethod.Post, Boleto + "cobranca-pre-efetivacao/pre-efetivacao-pagamento", body, ct).ConfigureAwait(false);
            if (previa.Value<decimal?>("valorCobrado") != m.ValorPagamento)
                throw new ArgumentException("ValorPagamento diverge do valorCobrado na prévia Bradesco; confira o boleto antes de enviar.");
            body.Remove("agencia");
            body["codigoAgencia"] = Origem.NumeroAgencia;
            body["indicadorFuncao"] = "1";
            body["transactionId"] = long.Parse(IdentidadeSalva(m).Value, CultureInfo.InvariantCulture);
            body["tipoIntermediador"] = "";
            var partes = PartesDocumento(Documento(Origem.CPF_CNPJ));
            body["cnpjCpfPtdor"] = long.Parse(partes[0], CultureInfo.InvariantCulture);
            body["filialCnpjPtdor"] = int.Parse(partes[1], CultureInfo.InvariantCulture);
            body["ctrlCnpjPtdor"] = int.Parse(partes[2], CultureInfo.InvariantCulture);
            var r = await Gateway.JsonAsync(HttpMethod.Post, Boleto + "cobranca-efetivacao/solicitacao/executar", body, ct).ConfigureAwait(false);
            m.NumeroDocumentoNoBanco = IdentidadeSalva(m).Value;
            var protocolo = (string)r["nroProtocolo"];
            ((MovimentoItemPagamentoTituloCodigoBarra)m.MovimentoItem).NumeroAutenticacaoPagamento = protocolo == "0" ? null : protocolo;
            // O exemplo oficial de efetivação ecoa a prévia. Confirmação de liquidação somente pela consulta de situação.
            m.SituacaoBancoBr = null;

            return m;
        }

        internal async Task<Movimento> ConvenioAsync(Movimento m, bool executar, CancellationToken ct)
        {
            var item = (MovimentoItemPagamentoConvenioCodigoBarra)m.MovimentoItem;
            var codigo = Digitos(item.CodigoBarra);
            if (codigo.Length != 44 || !codigo.StartsWith("8", StringComparison.Ordinal)) throw new ArgumentException("Bradesco exige código de arrecadação de 44 dígitos iniciado por 8.");
            var id = IdentidadeSalva(m).Value ?? Identidade(m, null, 9, false);
            var r = await Gateway.JsonAsync(HttpMethod.Post, Convenio + "pagamentoContaConsumo", new JObject
            {
                ["agencia"] = Origem.NumeroAgencia, ["conta"] = Origem.NumeroConta,
                ["digitoAgencia"] = DigitoAgencia(), ["digitoConta"] = Origem.DVConta,
                ["codigoBarras"] = codigo, ["dataDebito"] = Data(m.DataPagamento), ["descricaoCliente"] = m.NumeroDocumento,
                ["idTransacao"] = id, ["tipoConta"] = 1, ["tipoRegistro"] = executar ? 1 : 0, ["valorPrincipal"] = m.ValorPagamento
            }, ct).ConfigureAwait(false);
            AplicarConvenio(m, r, executar);
            if (!executar && item.ValorDocumento != m.ValorPagamento)
                throw new ArgumentException("ValorPagamento diverge do valorDebito validado pelo Bradesco.");
            if (executar) m.NumeroDocumentoNoBanco = id;
            return m;
        }

        internal async Task<Movimento> ConsultarAsync(Movimento m, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(m.NumeroDocumentoNoBanco)) throw new ArgumentException("Informe NumeroDocumentoNoBanco para consultar o pagamento.");
            switch (m.MovimentoItem)
            {
                case MovimentoItemPagamentoTituloCodigoBarra _:
                    if (!long.TryParse(m.NumeroDocumentoNoBanco, out var id) || id <= 0 || id > 999999999) throw new ArgumentException("Identificador de boleto Bradesco inválido.");
                    var r = await Gateway.JsonAsync(HttpMethod.Post, Boleto + "cobranca-pagamento-consulta/consulta-pagamento-especifico", new JObject
                    {
                        ["versaoLayout"] = "001", ["numeroBanco"] = 237, ["numeroAgencia"] = Origem.NumeroAgencia,
                        ["numeroConta"] = Origem.NumeroConta, ["numeroProtocolo"] = 0, ["dataPagamento"] = 0, ["transactionId"] = id
                    }, ct).ConfigureAwait(false);
                    var estado = r.Value<int?>("situacaoPagamento");

                    m.SituacaoBancoBr = estado == 1 || estado == 11 ? BancoBrSituacaoEnum.Efetivado : estado == 7 ? BancoBrSituacaoEnum.Agendado
                        : estado == 2 || estado == 8 ? BancoBrSituacaoEnum.Cancelado : estado == 3 || estado == 9 || estado == 12 ? BancoBrSituacaoEnum.Rejeitado : (BancoBrSituacaoEnum?)null;
                    m.DetalheRejeicaoBancoBr = (string)r["descrMotivo"];
                    if (r["valorPagamento"] != null) m.ValorPagamento = r.Value<decimal>("valorPagamento");
                    return m;
                case MovimentoItemTransferenciaPIX _:
                    AplicarPix(m, await _pix.JsonAsync(HttpMethod.Post, "v1/spi/consulta/transferencia/" + Uri.EscapeDataString(m.NumeroDocumentoNoBanco), null, ct).ConfigureAwait(false));
                    return m;
                case MovimentoItemTransferenciaTED _:
                    if (m.NumeroDocumentoNoBanco.Length < 7 || !long.TryParse(m.NumeroDocumentoNoBanco.Substring(0, 7), out var numero)) throw new ArgumentException("Chave TED Bradesco inválida.");
                    var ted = await Gateway.JsonAsync(HttpMethod.Post, "transferencia/ted/v1/consulta", new JObject { ["numeroDocumento"] = numero, ["dataOperacao"] = m.DataPagamento.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) }, ct).ConfigureAwait(false);
                    var status = (string)ted["statusMensagem"];

                    m.SituacaoBancoBr = status == "DEVOLVIDA" ? BancoBrSituacaoEnum.Rejeitado : (BancoBrSituacaoEnum?)null;
                    m.DetalheRejeicaoBancoBr = (string)ted["descricaoDaDevolucao"];
                    return m;
                case MovimentoItemPagamentoConvenioCodigoBarra _:
                    var url = Convenio + Origem.NumeroAgencia + "/" + Origem.NumeroConta + "/1?tipoConsulta=3&segmentoConsulta=99&dataInicial=" + Data(m.DataPagamento) + "&dataFinal=" + Data(m.DataPagamento) + "&idTransacao=" + Uri.EscapeDataString(m.NumeroDocumentoNoBanco);
                    AplicarConvenio(m, await Gateway.JsonAsync(HttpMethod.Get, url, null, ct).ConfigureAwait(false), true);
                    return m;
                default: throw new NotSupportedException("Consulta não documentada para esta modalidade Bradesco.");
            }
        }

        public async Task<Movimento> CancelarAsync(Movimento m, CancellationToken ct)
        {
            if (!(m.MovimentoItem is MovimentoItemPagamentoTituloCodigoBarra)) throw new NotSupportedException("Somente exclusão de agendamento de boleto está documentada para o Bradesco.");
            if (!long.TryParse(m.NumeroDocumentoNoBanco, out var id) || id <= 0 || id > 999999999) throw new ArgumentException("Informe o transactionId do boleto em NumeroDocumentoNoBanco.");
            var cancelamento = await Gateway.JsonAsync(HttpMethod.Post, Boleto + "cobranca-excluir-agendamento/exclusao/executar", new JObject
            {
                ["versao"] = 2, ["bancoDebito"] = 237, ["agenciaDebito"] = Origem.NumeroAgencia, ["digitoAgencia"] = DigitoAgencia(),
                ["contaDebito"] = Origem.NumeroConta, ["digitoConta"] = Origem.DVConta, ["numeroProtocolo"] = 0, ["idTransacao"] = id
            }, ct).ConfigureAwait(false);
            if (cancelamento.Value<int?>("status") != 200) throw new JsonSerializationException("Cancelamento sem confirmação de sucesso.");
            m.SituacaoBancoBr = BancoBrSituacaoEnum.Cancelado;

            return m;
        }

        private static void AplicarPix(Movimento m, JObject r)
        {
            var status = (string)r["status"];

            m.SituacaoBancoBr = status == "CONCLUIDO" ? BancoBrSituacaoEnum.Efetivado : status == "REJEITADO" ? BancoBrSituacaoEnum.Rejeitado : (BancoBrSituacaoEnum?)null;
            m.DetalheRejeicaoBancoBr = status == "REJEITADO" ? (string)r["mensagem"] : null;
            m.HorarioBancoBr = r.Value<DateTime?>("dataEfetivacao");
            if (m.MovimentoItem is MovimentoItemTransferenciaPIX item && r["recebedor"] is JObject recebedor)
            {
                item.TitularConfirmadoNome = (string)recebedor["nomeFavorecido"];
                item.TitularConfirmadoDocumento = (string)recebedor["cpfCnpj"];
                item.TipoChaveConfirmado = (string)recebedor["tipoChave"];
            }
        }

        private static void AplicarConvenio(Movimento m, JObject r, bool executar)
        {
            var dados = (r["regSaida"] as JArray)?.SingleOrDefault() as JObject;
            if (dados == null) throw new JsonSerializationException("Arrecadação sem registro único; reconcilie pelo idTransacao.");
            var item = (MovimentoItemPagamentoConvenioCodigoBarra)m.MovimentoItem;
            item.Autenticacao = (string)dados["autenticacaoBancaria"];
            item.ValorDocumento = dados.Value<decimal?>("valorDebito") ?? m.ValorPagamento;
            item.Convenio = (string)dados["codigoEmpresaConveniada"];
            m.SituacaoBancoBr = null;
            m.DetalheRejeicaoBancoBr = null;
            var original = (string)dados["situacaoConta"];

            // A validação (tipoRegistro=0) não executa pagamento, mesmo que devolva autenticação.
            if (!executar) return;

            if (!int.TryParse(original, out var situacao)) return;

            switch (situacao)
            {
                case 1:
                case 2:
                    m.SituacaoBancoBr = BancoBrSituacaoEnum.Agendado;
                    break;
                case 4:
                case 6:
                case 9:
                    m.SituacaoBancoBr = BancoBrSituacaoEnum.Efetivado;
                    break;
                case 0:
                case 5:
                    m.SituacaoBancoBr = BancoBrSituacaoEnum.Rejeitado;
                    m.DetalheRejeicaoBancoBr = situacao == 5 ? "Saldo insuficiente." : "Conta encerrada.";
                    break;
            }
        }

        private int DigitoAgencia() => int.TryParse(Origem.DVAgencia, out var digito) ? digito : throw new ArgumentException("Informe DVAgencia numérico para Bradesco.");
        private static long ContaComDigito(long conta, string dv)
            => conta > 0 && dv != null && dv.Length == 1 && char.IsDigit(dv[0]) ? long.Parse(conta.ToString(CultureInfo.InvariantCulture) + dv, CultureInfo.InvariantCulture) : throw new ArgumentException("Informe conta e dígito numérico.");
        private static string Documento(string s)
        {
            var doc = Digitos(s);
            if (doc.Length != 11 && doc.Length != 14) throw new ArgumentException("Informe CPF/CNPJ completo.");
            return doc;
        }
        private static string[] PartesDocumento(string doc) => doc.Length == 11
            ? new[] { doc.Substring(0, 9), "0000", doc.Substring(9) }
            : new[] { doc.Substring(0, 8), doc.Substring(8, 4), doc.Substring(12) };
        private static string TipoChave(FormaIniciacaoEnum tipo)
        {
            switch (tipo)
            {
                case FormaIniciacaoEnum.PIX_CPF_CNPJ: return "CPFCNPJ";
                case FormaIniciacaoEnum.PIX_Email: return "EMAIL";
                case FormaIniciacaoEnum.PIX_Telefone: return "TELEFONE";
                case FormaIniciacaoEnum.PIX_Aleatorio: return "EVP";
                default: throw new ArgumentException("Tipo de chave Pix inválido.");
            }
        }
    }
}
