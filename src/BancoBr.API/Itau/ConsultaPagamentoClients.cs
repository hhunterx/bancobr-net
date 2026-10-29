using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BancoBr.API.Base;
using BancoBr.API.Base.Models;
using BancoBr.Common.Instances;

namespace BancoBr.API.Itau
{
    /// <summary>Consulta boleto já incluído no SISPAG por outro canal; não inclui boleto por API.</summary>
    internal sealed class ConsultaBoletoClient : PagamentoBoletoApiBase
    {
        private readonly PagamentoPixClient _consulta;
        internal ConsultaBoletoClient(PagamentoPixClient consulta, Correntista origem) : base(null, origem) { _consulta = consulta; }
        private static NotSupportedException Indisponivel() => new NotSupportedException("A referência SISPAG Itaú permite apenas consultar boleto já incluído, pelo id do pagamento.");
        internal override Task<Movimento> ConsultarComprovantePorIdAsync(Movimento movimento, CancellationToken cancellationToken = default) => _consulta.ConsultarPagamentoAsync(movimento, cancellationToken);
        internal override Task<Movimento> ConsultarBoletoAsync(Movimento movimento, CancellationToken cancellationToken = default) => throw Indisponivel();
        internal override Task<Movimento> PagarBoletoAsync(Movimento movimento, string idempotencyKey, CancellationToken cancellationToken = default) => throw Indisponivel();
        internal override Task<Movimento> PagarBoletoComConsultaAsync(Movimento movimento, Guid idLancamento, CancellationToken cancellationToken = default) => throw Indisponivel();
        internal override Task<IReadOnlyList<PagamentoBoletoLoteResultadoItem>> PagarLoteBoletosAsync(IEnumerable<(Movimento Movimento, Guid IdLancamento)> itens, CancellationToken cancellationToken = default) => throw Indisponivel();
        internal override Task<Movimento> CancelarAgendamentoAsync(Movimento movimento, CancellationToken cancellationToken = default) => throw Indisponivel();
        internal override Task<Movimento> ConsultarComprovantePorIdempotencyAsync(string idempotencyKey, CancellationToken cancellationToken = default) => throw Indisponivel();
        internal override Task<IReadOnlyList<Movimento>> ConsultarBoletosDdaAsync(long numeroConta, DateTime dataInicial, DateTime dataFinal, SituacaoBoletoEnum situacao, TipoDataConsultaEnum tipoData, CancellationToken cancellationToken = default) => throw Indisponivel();
    }

    /// <summary>Consulta arrecadação já incluída no SISPAG por outro canal.</summary>
    internal sealed class ConsultaConvenioClient : PagamentoConvenioApiBase
    {
        private readonly PagamentoPixClient _consulta;
        internal ConsultaConvenioClient(PagamentoPixClient consulta, Correntista origem) : base(null, origem) { _consulta = consulta; }
        private static NotSupportedException Indisponivel() => new NotSupportedException("A referência SISPAG Itaú permite apenas consultar convênio já incluído, pelo id do pagamento.");
        internal override Task<Movimento> ConsultarComprovantePorNsuAsync(Movimento movimento, CancellationToken cancellationToken = default) => _consulta.ConsultarPagamentoAsync(movimento, cancellationToken);
        internal override Task<Movimento> ConsultarCodigoBarrasAsync(Movimento movimento, bool? recebimentoViaCaixa = null, int unidade = 0, CancellationToken cancellationToken = default) => throw Indisponivel();
        internal override Task<Movimento> PagarConvenioAsync(Movimento movimento, int unidade = 0, CancellationToken cancellationToken = default) => throw Indisponivel();
        internal override Task<IReadOnlyList<Movimento>> ConsultarPagamentosAsync(string codigoBarras, long instituicao, DateTime dataMovimento, long? transacao = null, CancellationToken cancellationToken = default) => throw Indisponivel();
        internal override Task<IReadOnlyList<ConciliacaoItem>> ConsultarConciliacoesAsync(long instituicao, DateTime dataMovimento, int? unidade = null, CancellationToken cancellationToken = default) => throw Indisponivel();
        internal override Task<IReadOnlyList<ConvenioHabilitado>> ConsultarConveniosHabilitadosAsync(long transacao, long instituicao, CancellationToken cancellationToken = default) => throw Indisponivel();
    }

    /// <summary>Consulta TED já incluída no SISPAG por outro canal.</summary>
    internal sealed class ConsultaTedClient : PagamentoTedApiBase
    {
        private readonly PagamentoPixClient _consulta;
        internal ConsultaTedClient(PagamentoPixClient consulta, Correntista origem) : base(null, origem) { _consulta = consulta; }
        internal override Task<Movimento> ConsultarTedAsync(Movimento movimento, CancellationToken cancellationToken = default) => _consulta.ConsultarPagamentoAsync(movimento, cancellationToken);
        internal override Task<Movimento> PagarTedAsync(Movimento movimento, string idempotencyKey, CancellationToken cancellationToken = default) => throw new NotSupportedException("A referência SISPAG Itaú não permite incluir TED por API.");
        internal override Task<Movimento> CancelarAgendamentoAsync(Movimento movimento, string idempotencyKey, CancellationToken cancellationToken = default) => throw new NotSupportedException("A referência SISPAG Itaú não expõe cancelamento por API.");
    }
}
