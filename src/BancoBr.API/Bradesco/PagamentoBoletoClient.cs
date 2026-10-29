using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BancoBr.API.Base;
using BancoBr.API.Base.Models;
using BancoBr.Common.Instances;

namespace BancoBr.API.Bradesco
{
    internal sealed class PagamentoBoletoClient : PagamentoBoletoApiBase
    {
        private readonly PagamentosClient _api;
        internal PagamentoBoletoClient(PagamentosClient api) : base(null, api.Gateway.Origem) { _api = api; }
        internal override Task<Movimento> ConsultarBoletoAsync(Movimento m, CancellationToken cancellationToken = default) => _api.ConsultarBoletoAsync(m, cancellationToken);
        internal override Task<Movimento> PagarBoletoAsync(Movimento m, string idempotencyKey, CancellationToken cancellationToken = default) => _api.EnviarAsync(m, idempotencyKey, cancellationToken);
        internal override Task<Movimento> PagarBoletoComConsultaAsync(Movimento m, Guid idLancamento, CancellationToken cancellationToken = default) => _api.EnviarAsync(m, idLancamento.ToString(), cancellationToken);
        internal override Task<Movimento> ConsultarComprovantePorIdAsync(Movimento m, CancellationToken cancellationToken = default) => _api.ConsultarAsync(m, cancellationToken);
        internal override Task<Movimento> CancelarAgendamentoAsync(Movimento m, CancellationToken cancellationToken = default) => _api.CancelarAsync(m, cancellationToken);
        internal override Task<Movimento> ConsultarComprovantePorIdempotencyAsync(string idempotencyKey, CancellationToken cancellationToken = default) => _api.ConsultarAsync(new Movimento { NumeroDocumentoNoBanco = idempotencyKey, MovimentoItem = new MovimentoItemPagamentoTituloCodigoBarra() }, cancellationToken);
        internal override async Task<IReadOnlyList<PagamentoBoletoLoteResultadoItem>> PagarLoteBoletosAsync(IEnumerable<(Movimento Movimento, Guid IdLancamento)> itens, CancellationToken cancellationToken = default)
        {
            var resultado = new List<PagamentoBoletoLoteResultadoItem>();
            foreach (var item in itens)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { await PagarBoletoComConsultaAsync(item.Movimento, item.IdLancamento, cancellationToken).ConfigureAwait(false); resultado.Add(PagamentoBoletoLoteResultadoItem.ComSucesso(item.Movimento, item.IdLancamento)); }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) { resultado.Add(PagamentoBoletoLoteResultadoItem.ComFalha(item.Movimento, item.IdLancamento, e)); }
            }
            return resultado;
        }
        internal override Task<IReadOnlyList<Movimento>> ConsultarBoletosDdaAsync(long numeroConta, DateTime dataInicial, DateTime dataFinal, SituacaoBoletoEnum situacao, TipoDataConsultaEnum tipoData, CancellationToken cancellationToken = default) => throw new NotSupportedException("DDA não consta nos manuais públicos Bradesco consultados.");
    }
}
