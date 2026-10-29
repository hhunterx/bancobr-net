using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BancoBr.API.Base;
using BancoBr.API.Core.Errors;
using BancoBr.Common.Enums;
using BancoBr.Common.Instances;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BancoBr.API.Itau
{
    /// <summary>Extrato e saldo da conta configurada, pela API Account Statement v1.</summary>
    internal sealed class ExtratoClient : ContaCorrenteApiBase, ISaldoApi
    {
        private readonly ItauHttpGateway _gateway;
        private readonly string _statementId;

        internal ExtratoClient(ItauHttpGateway gateway, Correntista origem) : base(null, origem)
        {
            _gateway = gateway;
            _statementId = PagamentoPixClient.Agencia(origem.NumeroAgencia) + PagamentoPixClient.Conta(origem.NumeroConta, origem.DVConta, 8);
        }

        internal override async Task<IReadOnlyList<Movimento>> ConsultarExtratoAsync(long numeroContaCorrente, int mes, int ano, int? diaInicial, int? diaFinal, bool agruparCnab, CancellationToken cancellationToken = default)
        {
            if (agruparCnab) throw new NotSupportedException("O agrupamento CNAB não é oferecido por esta integração Itaú.");
            if (numeroContaCorrente != Origem.NumeroConta) throw new ArgumentException("A conta consultada deve corresponder a ConfiguracaoAPI.Correntista.");
            var inicio = new DateTime(ano, mes, diaInicial ?? 1);
            var fim = new DateTime(ano, mes, diaFinal ?? DateTime.DaysInMonth(ano, mes));
            if (inicio > fim) throw new ArgumentException("Período invertido.");
            var caminho = "statements/" + _statementId;
            var url = caminho + "?type=current_account&start_date=" + inicio.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) +
                "&end_date=" + fim.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "&page=1&page_size=8000&show_pending_events=false";
            var visitadas = new HashSet<string>(StringComparer.Ordinal);
            var movimentos = new List<Movimento>();
            var paginaAnterior = 0;
            while (true)
            {
                var uri = _gateway.Resolver(url);
                if (uri.AbsolutePath != new Uri(_gateway.BaseUri, caminho).AbsolutePath || !visitadas.Add(uri.AbsoluteUri))
                    throw new JsonSerializationException("Paginação Itaú repetida ou de outra conta.");
                var r = await _gateway.JsonAsync(HttpMethod.Get, uri.AbsoluteUri, null, cancellationToken).ConfigureAwait(false);
                var data = r["data"];
                // O schema usa objeto; o guia adicional documenta lista de grupos de eventos.
                var grupos = data is JObject objeto ? new[] { objeto } : (data as JArray)?.OfType<JObject>().ToArray();
                if (grupos == null) throw new JsonSerializationException("Extrato Itaú sem data válido.");
                foreach (var grupo in grupos)
                {
                    var eventos = grupo["events"] as JArray ?? throw new JsonSerializationException("Extrato Itaú sem events.");
                    foreach (var evento in eventos)
                    {
                        var operacao = (string)evento["operation"];
                        if (operacao != "C" && operacao != "D") throw new JsonSerializationException("Operação de extrato Itaú desconhecida.");
                        var valor = Valor(evento["amount"]);
                        var dataContabil = (string)evento["date"]?["accounting"];
                        var dataEvento = (string)evento["date"]?["event"];
                        var dataLancamento = dataContabil != null
                            ? DateTime.ParseExact(dataContabil, "yyyy-MM-dd", CultureInfo.InvariantCulture)
                            : DateTimeOffset.Parse(dataEvento ?? throw new JsonSerializationException("Evento Itaú sem data."), CultureInfo.InvariantCulture).DateTime;
                        movimentos.Add(new Movimento
                        {
                            Moeda = "BRL", MovimentoItem = new MovimentoItemExtratoTransacao
                            {
                                TransactionId = (string)evento["id"] ?? throw new JsonSerializationException("Evento Itaú sem id."),
                                Data = dataLancamento,
                                Tipo = operacao == "C" ? BancoBrTipoCreditoDebitoEnum.Credito : BancoBrTipoCreditoDebitoEnum.Debito,
                                Valor = Math.Abs(valor),
                                Descricao = (string)evento["literal"]?["complete"] ?? (string)evento["literal"]?["shortened"],
                                DescricaoInformacaoComplementar = (string)evento["origin"]?["complement"],
                                NumeroDocumento = (string)evento["origin"]?["identifier"],
                                CpfCnpj = (string)evento["counterpart"]?["document"]
                            }
                        });
                    }
                }
                var paginacao = r["pagination"] ?? (data as JObject)?["pagination"];
                if (paginacao == null) throw new JsonSerializationException("Extrato Itaú sem paginação.");
                var pagina = paginacao.Value<int?>("page") ?? throw new JsonSerializationException("Paginação Itaú sem page.");
                var total = paginacao.Value<int?>("total_pages") ?? throw new JsonSerializationException("Paginação Itaú sem total_pages.");
                if (pagina != paginaAnterior + 1 || total < 0 || (total > 0 && pagina > total))
                    throw new JsonSerializationException("Página Itaú divergente da sequência solicitada.");
                paginaAnterior = pagina;
                var next = (string)paginacao["links"]?["next"];
                if (pagina >= total || string.IsNullOrEmpty(next)) break;
                url = next;
            }
            return movimentos;
        }

        public async Task<Movimento> ConsultarSaldoAsync(CancellationToken cancellationToken)
        {
            var r = await _gateway.JsonAsync(HttpMethod.Get, "balances", null, cancellationToken).ConfigureAwait(false);
            var contas = r["data"] as JArray ?? throw new JsonSerializationException("Saldo Itaú sem contas.");
            var conta = contas.SingleOrDefault(c => ((string)c["statementId"])?.PadLeft(12, '0') == _statementId);
            if (conta == null) throw new JsonSerializationException("Saldo Itaú não contém a conta configurada.");
            if (conta["error"] != null)
                throw new BancoApiException(206, new[] { new MensagemErro { Codigo = "saldo_indisponivel", Mensagem = "Saldo da conta Itaú indisponível na resposta parcial." } });
            var saldos = conta["balances"] as JArray ?? throw new JsonSerializationException("Conta Itaú sem balances.");
            var total = saldos.SingleOrDefault(x => (string)x["type"] == "saldo_total") ?? throw new JsonSerializationException("Conta Itaú sem saldo total.");
            var bloqueado = saldos.SingleOrDefault(x => (string)x["type"] == "saldo_bloqueado");
            return new Movimento
            {
                MovimentoItem = new MovimentoItemExtratoSaldo
                {
                    SaldoAtual = Valor(total["amount"]),
                    SaldoBloqueado = bloqueado == null ? 0 : Valor(bloqueado["amount"])
                }
            };
        }

        private static decimal Valor(JToken amount)
        {
            if ((string)amount?["currency"] != "BRL" || amount?["value"] == null)
                throw new JsonSerializationException("Valor Itaú ausente ou em moeda diferente de BRL.");
            return amount["value"].Value<decimal>();
        }
    }
}
