using System.Net;
using System.Text;
using BancoBr.API;
using BancoBr.API.Base.Models;
using BancoBr.API.Core.Errors;
using BancoBr.API.Core.OAuth;
using BancoBr.Common.Enums;
using BancoBr.Common.Instances;
using BancoBr.Tests.Sicoob;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BancoBr.Tests.Bradesco
{
    public class BradescoComprovanteTests
    {
        private static BancoApi Conectar(SequencedFakeHttpMessageHandler handler) => BancoApi.ConectarBradesco(
            new ConfiguracaoAPI { Correntista = new Correntista { Banco = 237, NumeroAgencia = 1234, DVAgencia = "0", NumeroConta = 12345, DVConta = "6", Nome = "Empresa fictícia" } },
            null, new StaticAccessTokenProvider("token-ficticio"), new HttpClient(handler));

        private static Movimento Movimento(bool boleto = false) => new()
        {
            NumeroDocumento = "123456", NumeroDocumentoNoBanco = "123456", DataPagamento = new DateTime(2026, 10, 28),
            MovimentoItem = boleto ? new MovimentoItemPagamentoTituloCodigoBarra { NumeroAutenticacaoPagamento = "987654" } : new MovimentoItemPagamentoConvenioCodigoBarra()
        };

        private static JObject Conta(int tipo = 3, int situacao = 6) => new()
        {
            ["tipoComprovante"] = tipo, ["situacaoConta"] = situacao, ["codigoBarras"] = "846" + new string('0', 41),
            ["dataDebito"] = "2026-10-28", ["valorDebito"] = 150, ["autenticacaoBancaria"] = "987654",
            ["nomeFantasia"] = "Água e saneamento", ["identificacaoPagamento"] = "CONTA DE CONSUMO", ["numeroIdentidade"] = "Código do cliente", ["identidadeDocumento"] = "1234",
            ["dataVencimentoOrgao"] = "2026-10-30", ["valorTributo"] = 150, ["multa"] = 2, ["juros"] = 1, ["descontos"] = 3
        };

        private static JObject Boleto(int situacao = 1) => new()
        {
            ["situacaoPagamento"] = situacao, ["nomeInstituicaoRecebedora"] = "BANCO BRADESCO S.A.",
            ["digitavel1"] = "23790.00000 00000.000000 00000.000000 0", ["digitavel2"] = "00000000015000",
            ["nomeBancoDestino"] = "BANCO FICTÍCIO", ["icdentTitloDda"] = "Beneficiário fictício", ["nomeFantasiaBeneficiario"] = "Loja fictícia",
            ["ccnpjCdentCobr"] = "12345678000100", ["isacdo"] = "Pagador fictício", ["ccgcCpfSacdo"] = "12345678900",
            ["cnpjCpfCtoPgto"] = "12345678000100", ["dataVencimento"] = 20261030, ["valorTituloOritem"] = 150,
            ["valorPagamento"] = 150, ["horaTransacao"] = 95256, ["valorJurosTitulo"] = 1, ["valorMultaTitulo"] = 2,
            ["valorDescontoTitulo"] = 1, ["vabtmtTitloCalcd"] = 1, ["valorBonusTitulo"] = 1
        };

        private static string RespostaConta(JObject dados) => new JObject { ["regSaida"] = new JArray(dados) }.ToString();
        private static string TextoPdf(byte[] pdf)
        {
            var texto = Encoding.ASCII.GetString(pdf);
            Assert.StartsWith("%PDF-1.4", texto);
            Assert.EndsWith("%%EOF\n", texto);
            return string.Join("\n", System.Text.RegularExpressions.Regex.Matches(texto, "<([0-9A-F]*)> Tj")
                .Select(m => Encoding.Latin1.GetString(Convert.FromHexString(m.Groups[1].Value))));
        }

        [Theory]
        [InlineData(2)]
        [InlineData(3)]
        public async Task ConsultarComprovanteAsync_ArrecadacaoEfetivada_PreenchePdf(int tipo)
        {
            var handler = new SequencedFakeHttpMessageHandler((HttpStatusCode.OK, RespostaConta(Conta(tipo))));
            var movimento = Movimento();
            Assert.True(await Conectar(handler).ConsultarComprovanteAsync(movimento));
            var texto = TextoPdf(movimento.ArquivoComprovantePDF);
            Assert.Contains("Água e saneamento", texto);
            Assert.Contains("Autenticação bancária: 987654", texto);
            Assert.Contains("R$ 150,00", texto);
            Assert.Equal(BancoBrSituacaoEnum.Efetivado, movimento.SituacaoBancoBr);
            Assert.Contains("idTransacao=123456", Assert.Single(handler.Requests).RequestUri!.Query);
        }

        [Fact]
        public async Task ConsultarComprovanteAsync_BoletoEfetivado_PreenchePdf()
        {
            var handler = new SequencedFakeHttpMessageHandler((HttpStatusCode.OK, Boleto().ToString()));
            var movimento = Movimento(true);
            Assert.True(await Conectar(handler).ConsultarComprovanteAsync(movimento));
            var texto = TextoPdf(movimento.ArquivoComprovantePDF);
            Assert.Contains("Beneficiário fictício", texto);
            Assert.Contains("09:52:56", texto);
            Assert.Contains("987654", texto);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(5)]
        [InlineData(7)]
        public async Task ConsultarComprovanteAsync_ArrecadacaoNaoEfetivada_LimpaPdfERetornaFalse(int situacao)
        {
            var handler = new SequencedFakeHttpMessageHandler((HttpStatusCode.OK, RespostaConta(Conta(situacao: situacao))));
            var movimento = Movimento();
            movimento.ArquivoComprovantePDF = new byte[] { 1 };
            Assert.False(await Conectar(handler).ConsultarComprovanteAsync(movimento));
            Assert.Null(movimento.ArquivoComprovantePDF);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task ConsultarComprovanteAsync_DadosObrigatoriosAusentes_RetornaFalse(bool boleto)
        {
            var dados = boleto ? Boleto() : Conta();
            dados.Remove(boleto ? "digitavel1" : "autenticacaoBancaria");
            var handler = new SequencedFakeHttpMessageHandler((HttpStatusCode.OK, boleto ? dados.ToString() : RespostaConta(dados)));
            var movimento = Movimento(boleto);
            Assert.False(await Conectar(handler).ConsultarComprovanteAsync(movimento));
            Assert.Null(movimento.ArquivoComprovantePDF);
        }

        [Theory]
        [InlineData(7)]
        [InlineData(2)]
        [InlineData(3)]
        public async Task ConsultarComprovanteAsync_BoletoNaoEfetivado_RetornaFalse(int situacao)
        {
            var handler = new SequencedFakeHttpMessageHandler((HttpStatusCode.OK, Boleto(situacao).ToString()));
            Assert.False(await Conectar(handler).ConsultarComprovanteAsync(Movimento(true)));
        }

        [Fact]
        public async Task ConsultarComprovanteAsync_BoletoSemProtocolo_NaoInventaAutenticacao()
        {
            var handler = new SequencedFakeHttpMessageHandler((HttpStatusCode.OK, Boleto().ToString()));
            var movimento = Movimento(true);
            ((MovimentoItemPagamentoTituloCodigoBarra)movimento.MovimentoItem).NumeroAutenticacaoPagamento = null;
            Assert.False(await Conectar(handler).ConsultarComprovanteAsync(movimento));
            Assert.Null(movimento.ArquivoComprovantePDF);
        }

        [Fact]
        public async Task ConsultarAgendamentoAsync_ContaFutura_ConsultaAgendados()
        {
            var handler = new SequencedFakeHttpMessageHandler((HttpStatusCode.OK, RespostaConta(Conta(situacao: 1))));
            var movimento = Movimento();
            movimento.DataPagamento = DateTime.Today.AddDays(5);
            await Conectar(handler).ConsultarAgendamentoAsync(movimento);
            Assert.Contains("tipoConsulta=4", Assert.Single(handler.Requests).RequestUri!.Query);
            Assert.Equal(BancoBrSituacaoEnum.Agendado, movimento.SituacaoBancoBr);
        }

        [Fact]
        public async Task EnviarAgendamentoAsync_ContaPassada_RejeitaAntesDoHttp()
        {
            var handler = new SequencedFakeHttpMessageHandler();
            var movimento = Movimento();
            movimento.DataPagamento = DateTime.Today.AddDays(-1);
            movimento.ValorPagamento = 150;
            await Assert.ThrowsAsync<ArgumentException>(() => Conectar(handler).EnviarAgendamentoAsync(movimento));
            Assert.Empty(handler.Requests);
        }

        [Fact]
        public async Task ConsultarComprovanteAsync_TributoEspecializado_InformaLimitacao()
        {
            var handler = new SequencedFakeHttpMessageHandler((HttpStatusCode.OK, RespostaConta(Conta(14))));
            await Assert.ThrowsAsync<NotSupportedException>(() => Conectar(handler).ConsultarComprovanteAsync(Movimento()));
        }

        [Fact]
        public async Task ConsultarComprovanteAsync_ErroHttp_PropagaSemGerarPdf()
        {
            var handler = new SequencedFakeHttpMessageHandler((HttpStatusCode.ServiceUnavailable, "{}"));
            await Assert.ThrowsAsync<BancoApiException>(() => Conectar(handler).ConsultarComprovanteAsync(Movimento()));
        }

        [Fact]
        public async Task ConsultarComprovanteAsync_Cancelado_PropagaCancelamento()
        {
            var handler = new SequencedFakeHttpMessageHandler();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Conectar(handler).ConsultarComprovanteAsync(Movimento(), new CancellationToken(true)));
            Assert.Empty(handler.Requests);
        }

        [Fact]
        public async Task EnviarAgendamentoAsync_ContaFutura_ValidaEAgendaSemConfirmarLiquidacao()
        {
            var handler = new SequencedFakeHttpMessageHandler(
                (HttpStatusCode.OK, RespostaConta(Conta(situacao: 1))), (HttpStatusCode.OK, RespostaConta(Conta(situacao: 1))));
            var movimento = Movimento();
            movimento.NumeroDocumentoNoBanco = null;
            movimento.DataPagamento = DateTime.Today.AddDays(5);
            movimento.ValorPagamento = 150;
            movimento.MovimentoItem = new MovimentoItemPagamentoConvenioCodigoBarra { CodigoBarra = "8" + new string('0', 43) };
            await Conectar(handler).EnviarAgendamentoAsync(movimento);
            Assert.Equal(BancoBrSituacaoEnum.Agendado, movimento.SituacaoBancoBr);
            Assert.Equal(2, handler.Bodies.Count);
            Assert.Equal(0, (int)JObject.Parse(handler.Bodies[0]!)["tipoRegistro"]!);
            Assert.Equal(1, (int)JObject.Parse(handler.Bodies[1]!)["tipoRegistro"]!);
            Assert.All(handler.Bodies, body => Assert.Equal(movimento.DataPagamento.ToString("yyyy-MM-dd"), (string)JObject.Parse(body!)["dataDebito"]!));
        }
    }
}
