using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BancoBr.API.Base;
using BancoBr.API.Core.Operacoes;
using BancoBr.Common.Enums;
using BancoBr.Common.Instances;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BancoBr.API.Bradesco
{
    internal sealed class ExtratoClient : ContaCorrenteApiBase, ISaldoApi
    {
        private readonly BradescoHttpGateway _gateway;
        internal ExtratoClient(BradescoHttpGateway gateway) : base(null, gateway.Origem) { _gateway = gateway; }
        private string Conta => "agencia=" + Origem.NumeroAgencia + "&conta=" + Origem.NumeroConta;
        // O sandbox rejeita tipoOperacao com HTTP 404; os exemplos oficiais omitem esse campo opcional.
        private string TipoOperacao => _gateway.Homologacao ? "" : "&tipoOperacao=1";
        internal static decimal Valor(JToken valor, string sinal = null)
        {
            var texto = (string)valor;
            if (string.IsNullOrWhiteSpace(texto)) throw new JsonSerializationException("Valor monetário Bradesco ausente.");
            var numero = decimal.Parse(texto, NumberStyles.Number, texto.Contains(",") ? CultureInfo.GetCultureInfo("pt-BR") : CultureInfo.InvariantCulture);
            return sinal == "-" ? -Math.Abs(numero) : numero;
        }

        public async Task<Movimento> ConsultarSaldoAsync(CancellationToken ct)
        {
            var r = await _gateway.JsonAsync(HttpMethod.Get, "v1/fornecimento-saldos-contas/saldos?" + Conta + TipoOperacao, null, ct).ConfigureAwait(false);
            var cc = r["saldoCC"] as JObject ?? throw new JsonSerializationException("Resposta sem saldoCC.");
            BradescoHttpGateway.ValidarConta(cc);
            var produtos = cc["lstLancamentosSaldos"] as JArray ?? throw new JsonSerializationException("Resposta sem produtos de saldo.");
            // 998 identifica CTA CORRENTE; não confundir com disponível (999), que pode incluir limite.
            var saldo = produtos.FirstOrDefault(x => x.Value<int?>("codigoProduto") == 998)
                ?? throw new JsonSerializationException("Resposta sem saldo da conta corrente (produto 998).");
            return new Movimento { MovimentoItem = new MovimentoItemExtratoSaldo { SaldoAtual = Valor(saldo["valorLancamento"], (string)saldo["sinalSaldo"]) } };
        }

        internal override async Task<IReadOnlyList<Movimento>> ConsultarExtratoAsync(long numeroContaCorrente, int mes, int ano, int? diaInicial, int? diaFinal, bool agruparCnab, CancellationToken cancellationToken = default)
        {
            if (agruparCnab) throw new NotSupportedException("Bradesco não documenta agrupamento CNAB nesta API.");
            if (numeroContaCorrente != Origem.NumeroConta) throw new ArgumentException("Informe a conta da ConfiguracaoAPI.Correntista.", nameof(numeroContaCorrente));
            var inicio = new DateTime(ano, mes, diaInicial ?? 1);
            var fim = new DateTime(ano, mes, diaFinal ?? DateTime.DaysInMonth(ano, mes));
            if (inicio > fim) throw new ArgumentException("Período invertido.");
            var r = await _gateway.JsonAsync(HttpMethod.Get, "v1/fornecimento-extratos-contas/extratos?" + Conta + "&tipo=cc&dataInicio=" + inicio.ToString("ddMMyyyy", CultureInfo.InvariantCulture)
                + "&dataFim=" + fim.ToString("ddMMyyyy", CultureInfo.InvariantCulture) + TipoOperacao, null, cancellationToken).ConfigureAwait(false);
            var movimentos = new List<Movimento>();
            var reconhecido = false;
            // O banco separa histórico, últimos lançamentos e futuros. Futuros não são movimentos liquidados.
            foreach (var nome in new[] { "extratoPorPeriodo", "extratoUltimosLancamentos" })
            {
                if (!(r[nome] is JObject bloco)) continue;
                reconhecido = true;
                BradescoHttpGateway.ValidarConta(bloco);
                IEnumerable<JToken> itens = bloco["lstLancamentoMensal"] as JArray;
                if (itens == null && bloco["listaLancamentos"] is JObject grupos)
                    itens = grupos.Properties().SelectMany(p => p.Value is JArray a ? a.AsEnumerable() : Enumerable.Empty<JToken>());
                if (itens == null) throw new JsonSerializationException("Extrato sem lista de lançamentos.");
                foreach (var item in itens)
                {
                    var descricao = (string)item["descritivoLancamentoCompleto"] ?? (string)item["descritivoLancamentoAbreviado"];
                    if ((string)item["identificacaoSubCodigo"] == "S" || (string)item["tipoLancamento"] == "01" || (string)item["tipoLancamento"] == "04" || (string)item["tipoLancamento"] == "05") continue;
                    var data = DateTime.ParseExact((string)item["dataLancamento"], "dd/MM/yyyy", CultureInfo.InvariantCulture);
                    if (data < inicio || data > fim) continue;
                    var sinal = (string)item["sinalLancamento"];
                    if (sinal != "+" && sinal != "-") throw new JsonSerializationException("Sinal do lançamento desconhecido.");
                    var transacao = new MovimentoItemExtratoTransacao
                    {
                        Data = data, NumeroDocumento = (string)item["numeroDocumento"], Descricao = descricao,
                        DescricaoInformacaoComplementar = (string)item["segundaLinhalLancamento"],
                        Valor = Math.Abs(Valor(item["valorLancamento"])),
                        Tipo = sinal == "-" ? BancoBrTipoCreditoDebitoEnum.Debito : BancoBrTipoCreditoDebitoEnum.Credito
                    };
                    if (item["valorSaldoAposLancamento"] != null)
                        MetadadosOperacao.De(transacao).SaldoAposLancamento = Valor(item["valorSaldoAposLancamento"], (string)item["sinalSaldo"]);
                    movimentos.Add(new Movimento { MovimentoItem = transacao });
                }
            }
            if (!reconhecido) throw new JsonSerializationException("Resposta de extrato não reconhecida.");
            return movimentos;
        }
    }
}
