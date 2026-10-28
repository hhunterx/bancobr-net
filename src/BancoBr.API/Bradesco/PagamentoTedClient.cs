using System.Threading;
using System.Threading.Tasks;
using BancoBr.API.Base;
using BancoBr.Common.Instances;

namespace BancoBr.API.Bradesco
{
    internal sealed class PagamentoTedClient : PagamentoTedApiBase
    {
        private readonly PagamentosClient _api;
        internal PagamentoTedClient(PagamentosClient api) : base(null, api.Gateway.Origem) { _api = api; }
        internal override Task<Movimento> PagarTedAsync(Movimento movimento, string idempotencyKey, CancellationToken cancellationToken = default) => _api.EnviarAsync(movimento, idempotencyKey, cancellationToken);
        internal override Task<Movimento> ConsultarTedAsync(Movimento movimento, CancellationToken cancellationToken = default) => _api.ConsultarAsync(movimento, cancellationToken);
        internal override Task<Movimento> CancelarAgendamentoAsync(Movimento movimento, string idempotencyKey, CancellationToken cancellationToken = default) => _api.CancelarAsync(movimento, cancellationToken);
    }
}
