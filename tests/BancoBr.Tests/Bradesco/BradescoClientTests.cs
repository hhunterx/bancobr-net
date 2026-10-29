using System.Net;
using BancoBr.API;
using BancoBr.API.Base.Models;
using BancoBr.API.Core.Errors;
using BancoBr.API.Core.OAuth;
using BancoBr.Common.Enums;
using BancoBr.Common.Instances;
using BancoBr.Tests.Sicoob;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BancoBr.Tests.Bradesco
{
    public class BradescoClientTests
    {
        private static ConfiguracaoAPI Config(bool homologacao = true) => new()
        {
            AmbienteHomologacao = homologacao,
            Correntista = new Correntista { Banco = 237, NumeroAgencia = 1234, DVAgencia = "0", NumeroConta = 12345, DVConta = "6", Nome = "Empresa fictícia", CPF_CNPJ = "12.345.678/0001-00" }
        };
        private static BancoApi Conectar(SequencedFakeHttpMessageHandler h, bool homologacao = true)
            => BancoApi.ConectarBradesco(Config(homologacao), null, new StaticAccessTokenProvider("token-ficticio"), new HttpClient(h));
        private static SequencedFakeHttpMessageHandler Respostas(params string[] json)
            => new(json.Select(s => (HttpStatusCode.OK, (string?)s)).ToArray());
        private static Movimento Movimento(MovimentoItem item) => new()
        {
            NumeroDocumento = "123456", DataPagamento = DateTime.Today, ValorPagamento = 150m, MovimentoItem = item,
            Favorecido = new Favorecido { Nome = "Pessoa fictícia", CPF_CNPJ = "123.456.789-00" }
        };
        private static readonly string Validacao = "{\"status\":200,\"codigoRetorno\":0,\"codBarras\":\"23790000000000150000000000000000000000000000\",\"valorTitulo\":150,\"dataVencimento\":20261028,\"consultaCip\":\"S\",\"numeroCtrlCip\":\"controle\",\"nmCedente\":\"Beneficiario ficticio\"}";

        [Fact]
        public async Task Boleto_ValidaPreviaEnviaConsultaECancela_PreservaIdentidadeEConta()
        {
            var h = Respostas(Validacao, "{\"status\":200,\"valorCobrado\":150}", "{\"status\":200,\"nroProtocolo\":123}", "{\"status\":200,\"situacaoPagamento\":7}", "{\"status\":200}");
            var banco = Conectar(h);
            var m = Movimento(new MovimentoItemPagamentoTituloCodigoBarra { CodigoBarras = new string('0', 44) });
            var resultado = await banco.EnviarAgendamentoAsync(m);
            Assert.Same(m, resultado);
            Assert.Null(resultado.Situacao);
            Assert.Equal("123456", m.NumeroDocumentoNoBanco);
            Assert.EndsWith("cobranca-efetivacao/solicitacao/executar", h.Requests[2].RequestUri!.AbsolutePath);
            var body = JObject.Parse(h.Bodies[2]!);
            Assert.Equal(123456, (long)body["transactionId"]!);
            Assert.Equal(1234, (int)body["agenciaDebito"]!);
            Assert.Equal(12345, (int)body["contaDebito"]!);
            Assert.Equal("controle", (string)body["numeroCtrlCip"]!);
            var salvo = JsonConvert.SerializeObject(m);
            var consultado = JsonConvert.DeserializeObject<Movimento>(salvo)!;
            consultado.MovimentoItem = new MovimentoItemPagamentoTituloCodigoBarra();
            await banco.ConsultarAgendamentoAsync(consultado);
            Assert.Equal(BancoBrSituacaoEnum.Agendado, consultado.Situacao);
            Assert.Equal(123456, (long)JObject.Parse(h.Bodies[3]!)["transactionId"]!);
            await banco.CancelarAgendamentoAsync(consultado);
            Assert.Equal(2, (int)JObject.Parse(h.Bodies[4]!)["versao"]!);
            Assert.All(h.Requests, r => Assert.Equal("token-ficticio", r.Headers.Authorization!.Parameter));
        }

        [Fact]
        public async Task Boleto_ValorPreviaDiverge_NaoEfetiva()
        {
            var h = Respostas(Validacao, "{\"status\":200,\"valorCobrado\":151}");
            await Assert.ThrowsAsync<ArgumentException>(() => Conectar(h).EnviarAgendamentoAsync(Movimento(new MovimentoItemPagamentoTituloCodigoBarra { CodigoBarras = new string('0', 44) })));
            Assert.Equal(2, h.Requests.Count);
        }

        [Theory]
        [InlineData("abc")]
        [InlineData("1000000000")]
        [InlineData("0")]
        public async Task Boleto_IdentidadeInvalida_NaoEnvia(string documento)
        {
            var h = Respostas();
            var m = Movimento(new MovimentoItemPagamentoTituloCodigoBarra());
            m.NumeroDocumento = documento;
            await Assert.ThrowsAsync<ArgumentException>(() => Conectar(h).EnviarAgendamentoAsync(m));
            Assert.Empty(h.Requests);
        }

        [Theory]
        [InlineData("CONCLUIDO", BancoBrSituacaoEnum.Efetivado)]
        [InlineData("REJEITADO", BancoBrSituacaoEnum.Rejeitado)]
        [InlineData("EM_PROCESSAMENTO", null)]
        [InlineData("NOVO_ESTADO", null)]
        public async Task Pix_EnviaEConsulta_UsaIdTransacaoESituacaoDocumentada(string status, BancoBrSituacaoEnum? situacao)
        {
            var h = Respostas("{\"status\":\"" + status + "\",\"e2e\":\"E0000000000000000000000000000000\"}", "{\"status\":\"CONCLUIDO\"}");
            var banco = Conectar(h, false);
            var m = Movimento(new MovimentoItemTransferenciaPIX { TipoChavePIX = FormaIniciacaoEnum.PIX_Telefone, ChavePIX = "+5511999999999" });
            await banco.EnviarAgendamentoAsync(m);
            Assert.Equal(situacao, m.Situacao);
            var body = JObject.Parse(h.Bodies[0]!);
            Assert.Equal("qrpix.bradesco.com.br", h.Requests[0].RequestUri!.Host);
            Assert.Equal("TELEFONE", (string)body["recebedor"]!["tipoChave"]!);
            Assert.Equal("1234", (string)body["pagador"]!["agencia"]!);
            await banco.ConsultarAgendamentoAsync(m);
            Assert.EndsWith("/consulta/transferencia/123456", h.Requests[1].RequestUri!.AbsolutePath);
            Assert.Equal(BancoBrSituacaoEnum.Efetivado, m.Situacao);
        }

        [Fact]
        public async Task Pix_PorConta_MapeiaContaEIsPB()
        {
            var h = Respostas("{\"status\":\"EM_PROCESSAMENTO\"}");
            var m = Movimento(new MovimentoItemTransferenciaPIX { Banco = 341, Ispb = "12345678", NumeroAgencia = 42, NumeroConta = 98765, DVConta = "1", TipoConta = TipoContaEnum.ContaPoupanca });
            await Conectar(h).EnviarAgendamentoAsync(m);
            var r = JObject.Parse(h.Bodies[0]!)["recebedor"]!;
            Assert.Equal("AGENCIACONTA", (string)r["tipoChave"]!);
            Assert.Equal("0042", (string)r["agencia"]!);
            Assert.Equal("CONTA_POUPANCA", (string)r["tipoConta"]!);
        }

        [Fact]
        public async Task Convenio_ValidaAntesDeEfetivar_EConsultaPorIdentidade()
        {
            const string retorno = "{\"retorno\":\"000\",\"regSaida\":[{\"valorDebito\":150,\"autenticacaoBancaria\":12345,\"situacaoConta\":6}]}";
            var h = Respostas(retorno, retorno, retorno);
            var banco = Conectar(h);
            var m = Movimento(new MovimentoItemPagamentoConvenioCodigoBarra { CodigoBarra = "8" + new string('0', 43) });
            await banco.EnviarAgendamentoAsync(m);
            Assert.Equal(0, (int)JObject.Parse(h.Bodies[0]!)["tipoRegistro"]!);
            Assert.Equal(1, (int)JObject.Parse(h.Bodies[1]!)["tipoRegistro"]!);
            Assert.Equal("123456", (string)JObject.Parse(h.Bodies[1]!)["idTransacao"]!);
            Assert.Equal(BancoBrSituacaoEnum.Efetivado, m.Situacao);
            await banco.ConsultarAgendamentoAsync(m);
            Assert.Equal(HttpMethod.Get, h.Requests[2].Method);
            Assert.Contains("idTransacao=123456", h.Requests[2].RequestUri!.Query);
        }

        [Theory]
        [InlineData("01", BancoBrSituacaoEnum.Agendado)]
        [InlineData("02", BancoBrSituacaoEnum.Agendado)]
        [InlineData("04", BancoBrSituacaoEnum.Efetivado)]
        [InlineData("06", BancoBrSituacaoEnum.Efetivado)]
        [InlineData("09", BancoBrSituacaoEnum.Efetivado)]
        [InlineData("05", BancoBrSituacaoEnum.Rejeitado)]
        [InlineData("00", BancoBrSituacaoEnum.Rejeitado)]
        [InlineData("07", null)]
        [InlineData("08", null)]
        [InlineData("99", null)]
        [InlineData(null, null)]
        public async Task Convenio_AutenticacaoPresente_RespeitaSituacaoDoBanco(string? situacao, BancoBrSituacaoEnum? esperada)
        {
            var dados = new JObject { ["valorDebito"] = 150, ["autenticacaoBancaria"] = 12345, ["situacaoConta"] = situacao };
            var resposta = new JObject { ["retorno"] = "000", ["regSaida"] = new JArray(dados) }.ToString();
            var h = Respostas(resposta, resposta, resposta);
            var banco = Conectar(h);
            var m = Movimento(new MovimentoItemPagamentoConvenioCodigoBarra { CodigoBarra = "8" + new string('0', 43) });
            var envio = await banco.EnviarAgendamentoAsync(m);
            Assert.Equal(esperada, envio.Situacao);
            var consulta = await banco.ConsultarAgendamentoAsync(envio);
            Assert.Equal(esperada, consulta.Situacao);
        }

        [Theory]
        [InlineData("00")]
        [InlineData("01")]
        public async Task Conta_CodigosDeSucesso_RetornamSaldoEExtrato(string codigo)
        {
            var h = Respostas("{\"saldoCC\":{\"codigoRetorno\":\"" + codigo + "\",\"lstLancamentosSaldos\":[{\"codigoProduto\":998,\"valorLancamento\":\"150,00\",\"sinalSaldo\":\"+\"}]}}",
                "{\"extratoPorPeriodo\":{\"codigoRetorno\":\"" + codigo + "\",\"lstLancamentoMensal\":[{\"tipoLancamento\":\"02\",\"dataLancamento\":\"01/10/2026\",\"sinalLancamento\":\"+\",\"valorLancamento\":\"50,00\"}]}}");
            var banco = Conectar(h);
            Assert.Equal(150m, Assert.IsType<MovimentoItemExtratoSaldo>((await banco.ConsultarSaldoAsync()).MovimentoItem).SaldoAtual);
            Assert.Equal(50m, Assert.IsType<MovimentoItemExtratoTransacao>(Assert.Single(await banco.ConsultarExtratoAsync(12345, 10, 2026)).MovimentoItem).Valor);
        }

        [Theory]
        [InlineData("04")]
        [InlineData("08")]
        [InlineData("24")]
        public async Task Conta_CodigoDeErro_ContinuaRejeitado(string codigo)
        {
            var h = Respostas("{\"saldoCC\":{\"codigoRetorno\":\"" + codigo + "\",\"mensagem\":\"Falha fictícia\"}}");
            var erro = await Assert.ThrowsAsync<BancoApiException>(() => Conectar(h).ConsultarSaldoAsync());
            Assert.Equal(codigo, Assert.Single(erro.Mensagens).Codigo);
        }

        [Fact]
        public async Task Boleto_Codigo01_NaoUsaRegraDeSucessoDaConta()
        {
            var h = Respostas("{\"codigoRetorno\":\"01\",\"mensagem\":\"Falha fictícia\"}");
            var m = Movimento(new MovimentoItemPagamentoTituloCodigoBarra { CodigoBarras = new string('0', 44) });
            await Assert.ThrowsAsync<BancoApiException>(() => Conectar(h).EnviarAgendamentoAsync(m));
            Assert.Single(h.Requests);
        }

        [Fact]
        public async Task Ted_EnviaConsulta_PreservaChaveEDevolucao()
        {
            var h = Respostas("{\"codigoDeRetorno\":23,\"chaveUnicaParaApi\":\"12345672026-10-28-10.00.00\"}", "{\"statusMensagem\":\"DEVOLVIDA\",\"descricaoDaDevolucao\":\"CONTA INVALIDA\"}");
            var banco = Conectar(h);
            var m = Movimento(new MovimentoItemTransferenciaTED { Banco = 341, NumeroAgencia = 12, NumeroConta = 9876, DVConta = "5", CodigoFinalidadeTED = FinalidadeTEDEnum.CreditoEmConta });
            await banco.EnviarAgendamentoAsync(m);
            Assert.Null(m.Situacao);
            var body = JObject.Parse(h.Bodies[0]!);
            Assert.Equal(123456, (long)body["contaRemetenteComDigito"]!);
            Assert.Equal("0000", (string)body["numeroFilial"]!);
            await banco.ConsultarAgendamentoAsync(m);
            Assert.Equal(1234567, (long)JObject.Parse(h.Bodies[1]!)["numeroDocumento"]!);
            Assert.Equal(BancoBrSituacaoEnum.Rejeitado, m.Situacao);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task Saldo_UsaContaCorrenteESinal_SemSomarLimite(bool homologacao)
        {
            var h = Respostas("{\"saldoCC\":{\"codigoRetorno\":\"00\",\"lstLancamentosSaldos\":[{\"codigoProduto\":999,\"valorLancamento\":\"9.000,00\",\"sinalSaldo\":\"+\"},{\"codigoProduto\":998,\"valorLancamento\":\"1.234,56\",\"sinalSaldo\":\"-\"}]}}" );
            var r = await Conectar(h, homologacao).ConsultarSaldoAsync();
            Assert.Equal(-1234.56m, Assert.IsType<MovimentoItemExtratoSaldo>(r.MovimentoItem).SaldoAtual);
            Assert.Contains("agencia=1234&conta=12345", h.Requests[0].RequestUri!.Query);
            Assert.Equal(!homologacao, h.Requests[0].RequestUri!.Query.Contains("tipoOperacao=1"));
        }

        [Fact]
        public async Task Extrato_MapeiaHistorico_IgnoraSaldosEFuturos()
        {
            var h = Respostas("{\"extratoPorPeriodo\":{\"codigoRetorno\":\"00\",\"lstLancamentoMensal\":[{\"tipoLancamento\":\"01\"},{\"tipoLancamento\":\"04\"},{\"tipoLancamento\":\"02\",\"dataLancamento\":\"01/10/2026\",\"sinalLancamento\":\"-\",\"valorLancamento\":\"1.234,56\",\"valorSaldoAposLancamento\":\"10,00\",\"sinalSaldo\":\"-\",\"descritivoLancamentoCompleto\":\"Pagamento\",\"numeroDocumento\":\"123\"}]}}" );
            var r = await Conectar(h).ConsultarExtratoAsync(12345, 10, 2026);
            var item = Assert.IsType<MovimentoItemExtratoTransacao>(Assert.Single(r).MovimentoItem);
            Assert.Equal(1234.56m, item.Valor);
            Assert.Equal(BancoBrTipoCreditoDebitoEnum.Debito, item.Tipo);
            Assert.Contains("dataInicio=01102026&dataFim=31102026", h.Requests[0].RequestUri!.Query);
            Assert.DoesNotContain("tipoOperacao", h.Requests[0].RequestUri!.Query);
        }

        [Fact]
        public async Task ErroDeNegocioHttp200_InterrompePagamento()
        {
            var h = Respostas("{\"retorno\":\"015\",\"mensagem\":\"SALDO INSUFICIENTE\"}");
            var m = Movimento(new MovimentoItemPagamentoConvenioCodigoBarra { CodigoBarra = "8" + new string('0', 43) });
            var e = await Assert.ThrowsAsync<BancoApiException>(() => Conectar(h).EnviarAgendamentoAsync(m));
            Assert.Equal("015", e.Mensagens[0].Codigo);
            Assert.Single(h.Requests);
        }

        [Fact]
        public async Task NaoAutorizado_RenovaUmaVez_PreservaCorpo()
        {
            var h = new SequencedFakeHttpMessageHandler((HttpStatusCode.Unauthorized, "{}"), (HttpStatusCode.OK, "{\"status\":\"CONCLUIDO\"}"));
            await Conectar(h).EnviarAgendamentoAsync(Movimento(new MovimentoItemTransferenciaPIX { TipoChavePIX = FormaIniciacaoEnum.PIX_Email, ChavePIX = "teste@example.com" }));
            Assert.Equal(h.Bodies[0], h.Bodies[1]);
            Assert.Equal(2, h.Requests.Count);
        }

        [Fact]
        public async Task Autenticacao_UsaEndpointsETokensDistintos()
        {
            var h = Respostas("{\"access_token\":\"geral-ficticio\",\"expires_in\":3600}",
                "{\"saldoCC\":{\"codigoRetorno\":\"00\",\"lstLancamentosSaldos\":[{\"codigoProduto\":998,\"valorLancamento\":\"1,00\",\"sinalSaldo\":\"+\"}]}}",
                "{\"access_token\":\"pix-ficticio\",\"expires_in\":3600}", "{\"status\":\"CONCLUIDO\"}");
            var config = Config(false);
            config.ClientId = "id-ficticio";
            config.ClientSecret = "segredo-ficticio";
            var banco = BancoApi.ConectarBradesco(config, null, null, new HttpClient(h));
            await banco.ConsultarSaldoAsync();
            var m = Movimento(new MovimentoItemTransferenciaPIX { TipoChavePIX = FormaIniciacaoEnum.PIX_Email, ChavePIX = "teste@example.com" });
            m.NumeroDocumento = null;
            var id = Guid.NewGuid();
            await banco.EnviarAgendamentoAsync(m, id.ToString());
            Assert.Equal("https://openapi.bradesco.com.br/auth/server-mtls/v2/token", h.Requests[0].RequestUri!.AbsoluteUri);
            Assert.Equal("https://qrpix.bradesco.com.br/auth/server/oauth/token", h.Requests[2].RequestUri!.AbsoluteUri);
            Assert.Contains("grant_type=client_credentials", h.Bodies[0]);
            Assert.Contains("client_id=id-ficticio", h.Bodies[2]);
            Assert.Equal("geral-ficticio", h.Requests[1].Headers.Authorization!.Parameter);
            Assert.Equal("pix-ficticio", h.Requests[3].Headers.Authorization!.Parameter);
            Assert.Equal(id.ToString("N"), (string)JObject.Parse(h.Bodies[3]!)["idtransacao"]!);
        }

        [Fact]
        public async Task Pix_RetornoPerdido_ReenvioMantemIdentidade()
        {
            var h = Respostas("{\"status\":\"EM_PROCESSAMENTO\"}", "{\"status\":\"CONCLUIDO\"}");
            var banco = Conectar(h);
            var m = Movimento(new MovimentoItemTransferenciaPIX { TipoChavePIX = FormaIniciacaoEnum.PIX_Email, ChavePIX = "teste@example.com" });
            m.NumeroDocumento = null;
            await banco.EnviarAgendamentoAsync(m);
            m.NumeroDocumentoNoBanco = null;
            await banco.EnviarAgendamentoAsync(m);
            Assert.Equal((string)JObject.Parse(h.Bodies[0]!)["idtransacao"]!, (string)JObject.Parse(h.Bodies[1]!)["idtransacao"]!);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public async Task Pix_ReenvioPersistido_ConsultaIdentidadeOriginalSemNovoPagamento(int dias)
        {
            var h = Respostas("{\"status\":\"EM_PROCESSAMENTO\"}", "{\"status\":\"CONCLUIDO\"}");
            var m = Movimento(new MovimentoItemTransferenciaPIX { TipoChavePIX = FormaIniciacaoEnum.PIX_Email, ChavePIX = "teste@example.com" });
            m.NumeroDocumento = null;
            await Conectar(h).EnviarAgendamentoAsync(m);
            var identidade = m.NumeroDocumentoNoBanco;
            var recuperado = JsonConvert.DeserializeObject<Movimento>(JsonConvert.SerializeObject(m))!;
            recuperado.MovimentoItem = new MovimentoItemTransferenciaPIX();
            recuperado.DataPagamento = DateTime.Today.AddDays(dias);

            var resultado = await Conectar(h).EnviarAgendamentoAsync(recuperado);

            Assert.Same(recuperado, resultado);
            Assert.Equal(identidade, resultado.NumeroDocumentoNoBanco);
            Assert.Equal(BancoBrSituacaoEnum.Efetivado, resultado.Situacao);
            Assert.Equal(2, h.Requests.Count);
            Assert.EndsWith("/consulta/transferencia/" + identidade, h.Requests[1].RequestUri!.AbsolutePath);
        }

        [Fact]
        public async Task Pix_FalhaNoEnvio_PreservaIdentidadeParaRecuperacaoAposPersistencia()
        {
            var h = new SequencedFakeHttpMessageHandler(
                (HttpStatusCode.ServiceUnavailable, "{}"), (HttpStatusCode.OK, "{\"status\":\"CONCLUIDO\"}"));
            var m = Movimento(new MovimentoItemTransferenciaPIX { TipoChavePIX = FormaIniciacaoEnum.PIX_Email, ChavePIX = "teste@example.com" });
            m.NumeroDocumento = null;
            await Assert.ThrowsAsync<BancoApiException>(() => Conectar(h).EnviarAgendamentoAsync(m));
            var identidade = (string)JObject.Parse(h.Bodies[0]!)["idtransacao"]!;
            Assert.Equal(identidade, m.NumeroDocumentoNoBanco);
            var recuperado = JsonConvert.DeserializeObject<Movimento>(JsonConvert.SerializeObject(m))!;
            recuperado.MovimentoItem = new MovimentoItemTransferenciaPIX();

            await Conectar(h).EnviarAgendamentoAsync(recuperado);

            Assert.Equal(2, h.Requests.Count);
            Assert.EndsWith("/consulta/transferencia/" + identidade, h.Requests[1].RequestUri!.AbsolutePath);
        }

        [Fact]
        public async Task Pix_ConsultaDaTentativaFalha_NaoSolicitaOutraTransferencia()
        {
            var h = new SequencedFakeHttpMessageHandler((HttpStatusCode.NotFound, "{}"));
            var m = Movimento(new MovimentoItemTransferenciaPIX());
            m.NumeroDocumento = null;
            m.NumeroDocumentoNoBanco = "tentativa-original";

            await Assert.ThrowsAsync<BancoApiException>(() => Conectar(h).EnviarAgendamentoAsync(m));

            Assert.Equal("tentativa-original", m.NumeroDocumentoNoBanco);
            Assert.EndsWith("/consulta/transferencia/tentativa-original", Assert.Single(h.Requests).RequestUri!.AbsolutePath);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task Pix_ReenvioComIdentidadeDivergente_NaoEnvia(bool documento)
        {
            var h = Respostas();
            var m = Movimento(new MovimentoItemTransferenciaPIX { TipoChavePIX = FormaIniciacaoEnum.PIX_Email, ChavePIX = "teste@example.com" });
            m.NumeroDocumentoNoBanco = "original";
            m.NumeroDocumento = documento ? "outra" : null;
            await Assert.ThrowsAsync<ArgumentException>(() => Conectar(h).EnviarAgendamentoAsync(m, documento ? null : "outra"));
            Assert.Empty(h.Requests);
        }

        [Theory]
        [InlineData("1", null, "1")]
        [InlineData("999999999", null, "999999999")]
        [InlineData(null, "123456789", "123456789")]
        [InlineData("123456789", "chave-ignorada", "123456789")]
        public async Task Boleto_IdentidadeValida_PriorizaNumeroDocumento(string? documento, string? chave, string esperado)
        {
            var h = Respostas(Validacao, "{\"status\":200,\"valorCobrado\":150}", "{\"status\":200}");
            var m = Movimento(new MovimentoItemPagamentoTituloCodigoBarra { CodigoBarras = new string('0', 44) });
            m.NumeroDocumento = documento;
            await Conectar(h).EnviarAgendamentoAsync(m, chave);
            Assert.Equal(esperado, m.NumeroDocumentoNoBanco);
            Assert.Equal(long.Parse(esperado), (long)JObject.Parse(h.Bodies[2]!)["transactionId"]!);
        }

        [Theory]
        [InlineData("1000000000")]
        [InlineData("11111111-2222-3333-4444-555555555555")]
        [InlineData("abc")]
        [InlineData("0")]
        public async Task Boleto_ChaveInvalidaSemNumeroDocumento_NaoEnvia(string chave)
        {
            var h = Respostas();
            var m = Movimento(new MovimentoItemPagamentoTituloCodigoBarra());
            m.NumeroDocumento = null;
            var erro = await Assert.ThrowsAsync<ArgumentException>(() => Conectar(h).EnviarAgendamentoAsync(m, chave));
            Assert.Contains("9", erro.Message);
            Assert.Empty(h.Requests);
        }

        [Fact]
        public async Task Pix_MovimentoPersistido_ConsultaSemReexecutar()
        {
            var h = Respostas("{\"status\":\"EM_PROCESSAMENTO\"}", "{\"status\":\"CONCLUIDO\"}");
            var banco = Conectar(h);
            var r = await banco.EnviarAgendamentoAsync(Movimento(new MovimentoItemTransferenciaPIX { TipoChavePIX = FormaIniciacaoEnum.PIX_Email, ChavePIX = "teste@example.com" }));
            var salvo = JsonConvert.SerializeObject(r);
            var consulta = JsonConvert.DeserializeObject<Movimento>(salvo)!;
            consulta.MovimentoItem = new MovimentoItemTransferenciaPIX();
            await banco.ConsultarAgendamentoAsync(consulta);
            Assert.Equal(BancoBrSituacaoEnum.Efetivado, consulta.Situacao);
            Assert.EndsWith("/consulta/transferencia/123456", h.Requests[1].RequestUri!.AbsolutePath);
        }

        [Fact]
        public async Task NaoAutorizadoPersistente_NaoRepeteIndefinidamente()
        {
            var h = new SequencedFakeHttpMessageHandler((HttpStatusCode.Unauthorized, "{}"), (HttpStatusCode.Unauthorized, "{\"codigo\":\"AUTH\",\"mensagem\":\"Token inválido\"}"));
            await Assert.ThrowsAsync<BancoApiException>(() => Conectar(h).ConsultarSaldoAsync());
            Assert.Equal(2, h.Requests.Count);
        }

        [Fact]
        public async Task Saldo_ProdutoAusente_NaoRetornaZeroComoSaldoReal()
        {
            var h = Respostas("{\"saldoCC\":{\"codigoRetorno\":\"00\",\"lstLancamentosSaldos\":[]}}");
            await Assert.ThrowsAsync<JsonSerializationException>(() => Conectar(h).ConsultarSaldoAsync());
        }

        [Fact]
        public async Task Convenio_ValorValidadoDiverge_NaoEfetiva()
        {
            var h = Respostas("{\"retorno\":\"000\",\"regSaida\":[{\"valorDebito\":151}]}");
            var m = Movimento(new MovimentoItemPagamentoConvenioCodigoBarra { CodigoBarra = "8" + new string('0', 43) });
            await Assert.ThrowsAsync<ArgumentException>(() => Conectar(h).EnviarAgendamentoAsync(m));
            Assert.Single(h.Requests);
        }

        [Fact]
        public async Task Pix_ValorComFracaoDeCentavo_NaoArredondaPagamento()
        {
            var h = Respostas();
            var m = Movimento(new MovimentoItemTransferenciaPIX());
            m.ValorPagamento = 1.001m;
            await Assert.ThrowsAsync<ArgumentException>(() => Conectar(h).EnviarAgendamentoAsync(m));
            Assert.Empty(h.Requests);
        }

        [Fact]
        public async Task Cancelamento_RespostaSemConfirmacao_NaoMarcaCancelado()
        {
            var h = Respostas("{}");
            var m = Movimento(new MovimentoItemPagamentoTituloCodigoBarra());
            m.NumeroDocumentoNoBanco = "123456";
            await Assert.ThrowsAsync<JsonSerializationException>(() => Conectar(h).CancelarAgendamentoAsync(m));
            Assert.NotEqual(BancoBrSituacaoEnum.Cancelado, m.Situacao);
        }

        [Fact]
        public async Task DdaQrCodeECancelamentoPix_NaoInventamEndpoints()
        {
            var h = Respostas();
            var banco = Conectar(h);
            await Assert.ThrowsAsync<NotSupportedException>(() => banco.ConsultarDDAAsync(12345, DateTime.Today, DateTime.Today, SituacaoBoletoEnum.EmAberto, TipoDataConsultaEnum.Vencimento));
            await Assert.ThrowsAsync<NotSupportedException>(() => banco.EnviarAgendamentoAsync(Movimento(new MovimentoItemPagamentoTituloPIXQRCode())));
            await Assert.ThrowsAsync<NotSupportedException>(() => banco.CancelarAgendamentoAsync(Movimento(new MovimentoItemTransferenciaPIX())));
            Assert.Empty(h.Requests);
        }
    }
}
