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

namespace BancoBr.Tests.Itau
{
    public class ItauClientTests
    {
        private const string Id = "11111111-1111-4111-8111-111111111111";
        private const string StatementId = "150001234567";
        private const string Incluido = "{\"status_pagamento\":\"Sucesso\",\"cod_pagamento\":\"" + Id + "\"}";
        private static ConfiguracaoAPI Config(bool homologacao = false) => new ConfiguracaoAPI
        {
            ClientId = "client-ficticio", ClientSecret = "segredo-ficticio", AmbienteHomologacao = homologacao,
            Correntista = new Correntista
            {
                Banco = (int)BancoEnum.Itau, NumeroAgencia = 1500, NumeroConta = 123456, DVConta = "7",
                TipoPessoa = TipoInscricaoCPFCNPJEnum.CNPJ, CPF_CNPJ = "11.111.111/0001-11"
            }
        };
        private static BancoApi Conectar(SequencedFakeHttpMessageHandler handler, bool homologacao = false, IAccessTokenProvider? tokens = null)
            => BancoApi.ConectarItau(Config(homologacao), null, tokens ?? new FakeOAuthTokenProvider(), new HttpClient(handler));
        private static Movimento Pix() => new Movimento
        {
            NumeroDocumento = "ERP-0001", DataPagamento = new DateTime(2026, 10, 28), ValorPagamento = 123.40m,
            MovimentoItem = new MovimentoItemTransferenciaPIX { ChavePIX = "fornecedor@example.com" }
        };
        private static string Detalhe(string status = "Efetuado", string id = Id) => new JObject
        {
            ["data"] = new JObject { ["dados_debito"] = new JObject { ["numero_agencia_debito"] = "1500", ["numero_conta_debito"] = "01234567" },
                ["dados_pagamento"] = new JObject { ["id_pagamento"] = id, ["status"] = status,
                ["motivo_rejeicao"] = new JArray() } }
        }.ToString();
        private static JObject Evento(string id, string operacao = "D") => new JObject
        {
            ["id"] = id, ["operation"] = operacao, ["reversal"] = true,
            ["date"] = new JObject { ["accounting"] = "2026-10-01", ["event"] = "2026-10-02T02:59:00Z" },
            ["amount"] = new JObject { ["value"] = 25.50m, ["currency"] = "BRL" },
            ["literal"] = new JObject { ["complete"] = "PIX FICTICIO" },
            ["origin"] = new JObject { ["identifier"] = "E-FICTICIO", ["complement"] = "Complemento" },
            ["counterpart"] = new JObject { ["document"] = "***.111.111-**" }
        };
        private static string Extrato(int pagina, string? next = null, bool lista = false, string operacao = "D")
        {
            var grupo = new JObject { ["events"] = new JArray(Evento("id-" + pagina, operacao)) };
            var paginacao = new JObject { ["page"] = pagina, ["total_pages"] = 2, ["links"] = new JObject { ["next"] = next ?? "" } };
            if (lista) return new JObject { ["data"] = new JArray(grupo), ["pagination"] = paginacao }.ToString();
            grupo["pagination"] = paginacao;
            return new JObject { ["data"] = grupo }.ToString();
        }
        private static JObject Saldo(string conta, decimal valor, bool erro = false) => erro
            ? new JObject { ["statementId"] = conta, ["error"] = new JObject { ["error"] = "Indisponível" } }
            : new JObject { ["statementId"] = conta, ["balances"] = new JArray(
                new JObject { ["type"] = "saldo_disponivel", ["amount"] = new JObject { ["value"] = valor - 10, ["currency"] = "BRL" } },
                new JObject { ["type"] = "saldo_total", ["amount"] = new JObject { ["value"] = valor, ["currency"] = "BRL" } },
                new JObject { ["type"] = "saldo_bloqueado", ["amount"] = new JObject { ["value"] = 10, ["currency"] = "BRL" } }) };

        [Fact]
        public async Task Fachada_PixPorChave_IncluiComContaConfiguradaEConsultaAntesDeReenviar()
        {
            var handler = new SequencedFakeHttpMessageHandler((HttpStatusCode.OK, Incluido), (HttpStatusCode.OK, Detalhe()));
            var banco = Conectar(handler);
            var m = Pix();
            Assert.Same(m, await banco.EnviarAgendamentoAsync(m, "chave-erp"));
            Assert.Equal(BancoBrSituacaoEnum.Agendado, m.Situacao);
            Assert.Equal(Id, m.NumeroDocumentoNoBanco);
            var corpo = JObject.Parse(handler.Bodies[0]!);
            Assert.Equal(123.40m, (decimal)corpo["valor_pagamento"]!);
            Assert.Equal(JTokenType.Float, corpo["valor_pagamento"]!.Type);
            Assert.Equal("2026-10-28", (string?)corpo["data_pagamento"]);
            Assert.Equal("fornecedor@example.com", (string?)corpo["chave"]);
            Assert.Equal("01234567", (string?)corpo["pagador"]!["conta"]);
            Assert.Equal("1500", (string?)corpo["pagador"]!["agencia"]);
            Assert.Equal("11111111000111", (string?)corpo["pagador"]!["documento"]);
            Assert.Equal("Fornecedores", (string?)corpo["pagador"]!["modulo_sispag"]);
            Assert.Equal("client-ficticio", handler.Requests[0].Headers.GetValues("x-itau-apikey").Single());
            Assert.Equal("chave-erp", handler.Requests[0].Headers.GetValues("idempotency-key").Single());
            Assert.Equal("https://api.gateway.itau.com.br/sispag/v1/transferencias", handler.Requests[0].RequestUri!.AbsoluteUri);
            await banco.EnviarAgendamentoAsync(m, "outra-chave");
            Assert.Equal(HttpMethod.Get, handler.Requests[1].Method);
            Assert.EndsWith("/pagamentos_sispag/" + Id, handler.Requests[1].RequestUri!.AbsoluteUri);
            Assert.Equal(BancoBrSituacaoEnum.Efetivado, m.Situacao);
        }

        [Fact]
        public async Task Reenvio_AposErroDeRede_PreservaCorpoEIdempotencia()
        {
            var handler = new SequencedFakeHttpMessageHandler((HttpStatusCode.InternalServerError, "{}"), (HttpStatusCode.OK, Incluido));
            var banco = Conectar(handler);
            var m = Pix();
            await Assert.ThrowsAsync<BancoApiException>(() => banco.EnviarAgendamentoAsync(m, "primeira"));
            Assert.Null(m.NumeroDocumentoNoBanco);
            m.ValorPagamento = 999;
            await Assert.ThrowsAsync<InvalidOperationException>(() => banco.EnviarAgendamentoAsync(m, "segunda"));
            Assert.Single(handler.Requests);
            m.ValorPagamento = 123.40m;
            await banco.EnviarAgendamentoAsync(m, "segunda");
            Assert.Equal(handler.Bodies[0], handler.Bodies[1]);
            Assert.All(handler.Requests, r => Assert.Equal("primeira", r.Headers.GetValues("idempotency-key").Single()));
        }

        [Fact]
        public async Task Fachada_PixDados_MapeiaIspbDocumentoETipoConta()
        {
            var handler = new SequencedFakeHttpMessageHandler((HttpStatusCode.OK, Incluido));
            var m = Pix();
            m.Favorecido = new Favorecido { TipoPessoa = TipoInscricaoCPFCNPJEnum.CPF, CPF_CNPJ = "111.111.111-11" };
            m.MovimentoItem = new MovimentoItemTransferenciaPIX
            {
                Ispb = "12345678", NumeroAgencia = 1, NumeroConta = 100, DVConta = "2", TipoConta = TipoContaEnum.ContaPagamento
            };
            await Conectar(handler).EnviarAgendamentoAsync(m);
            var corpo = JObject.Parse(handler.Bodies[0]!);
            Assert.Null(corpo["chave"]);
            Assert.Equal("12345678", (string?)corpo["ispb"]);
            Assert.Equal("CP", (string?)corpo["tipo_identificacao_conta"]);
            Assert.Equal("0001", (string?)corpo["agencia_recebedor"]);
            Assert.Equal("1002", (string?)corpo["conta_recebedor"]);
            Assert.Equal("F", (string?)corpo["tipo_de_identificacao_do_recebedor"]);
            Assert.Equal("11111111111", (string?)corpo["identificacao_recebedor"]);
        }

        [Fact]
        public async Task Fachada_QrCode_EnviaEmvSemConsultarOuConfirmar()
        {
            var handler = new SequencedFakeHttpMessageHandler((HttpStatusCode.OK, Incluido));
            var m = Pix(); m.MovimentoItem = new MovimentoItemPagamentoTituloPIXQRCode { QrCode = "000201-EMV-FICTICIO", TXID = "txid-ficticio" };
            await Conectar(handler, true).EnviarAgendamentoAsync(m, "qr-erp");
            Assert.Single(handler.Requests);
            Assert.Equal("sandbox.devportal.itau.com.br", handler.Requests[0].RequestUri!.Host);
            var corpo = JObject.Parse(handler.Bodies[0]!);
            Assert.Equal("000201-EMV-FICTICIO", (string?)corpo["emv"]);
            Assert.Equal("txid-ficticio", (string?)corpo["txid"]);
        }

        [Theory]
        [InlineData("AE", BancoBrSituacaoEnum.Agendado)]
        [InlineData("A Efetuar", BancoBrSituacaoEnum.Agendado)]
        [InlineData("EF", BancoBrSituacaoEnum.Efetivado)]
        [InlineData("Efetuado", BancoBrSituacaoEnum.Efetivado)]
        [InlineData("NE", BancoBrSituacaoEnum.Rejeitado)]
        [InlineData("Não Efetuado", BancoBrSituacaoEnum.Rejeitado)]
        [InlineData("Novo status", BancoBrSituacaoEnum.NaoIntegrado)]
        public async Task Consulta_NormalizaEstadosSemInferirLiquidacao(string status, BancoBrSituacaoEnum esperado)
        {
            var handler = new SequencedFakeHttpMessageHandler((HttpStatusCode.OK, Detalhe(status)));
            var m = Pix(); m.NumeroDocumentoNoBanco = Id;
            await Conectar(handler).ConsultarAgendamentoAsync(m);
            Assert.Equal(esperado, m.Situacao);
        }

        [Theory]
        [InlineData("direto")]
        [InlineData("data")]
        [InlineData("value")]
        public async Task Consulta_AceitaEnvelopesDocumentados(string envelope)
        {
            var data = JObject.Parse(Detalhe())["data"]!;
            var body = envelope == "direto" ? data.ToString() : envelope == "data" ? Detalhe() : new JObject { ["value"] = new JObject { ["data"] = data } }.ToString();
            var handler = new SequencedFakeHttpMessageHandler((HttpStatusCode.OK, body));
            var m = Pix(); m.NumeroDocumentoNoBanco = Id;
            await Conectar(handler).ConsultarAgendamentoAsync(m);
            Assert.Equal(BancoBrSituacaoEnum.Efetivado, m.Situacao);
        }

        [Theory]
        [InlineData("{}")]
        [InlineData("{\"status_pagamento\":\"Sucesso\"}")]
        [InlineData("{\"status_pagamento\":\"NOVO\",\"cod_pagamento\":\"11111111-1111-4111-8111-111111111111\"}")]
        public async Task Inclusao_RespostaIncompleta_NaoMarcaSucesso(string body)
        {
            var handler = new SequencedFakeHttpMessageHandler((HttpStatusCode.OK, body));
            var m = Pix();
            await Assert.ThrowsAsync<JsonSerializationException>(() => Conectar(handler).EnviarAgendamentoAsync(m));
            Assert.Null(m.NumeroDocumentoNoBanco);
        }

        [Theory]
        [InlineData("{\"codigo\":\"931\",\"mensagem\":\"Conta inválida\"}", "931", "Conta inválida")]
        [InlineData("{\"status_pagamento\":\"Rejeitado\",\"motivo_recusa\":[{\"codigo\":\"852\",\"nome\":\"Agência inválida\"}]}", "852", "Agência inválida")]
        [InlineData("{\"codigo\":\"400\",\"campos\":[{\"campo\":\"data_pagamento\",\"mensagem\":\"Data inválida\"}]}", "400", "Data inválida")]
        public async Task Erro_PreservaCodigoEMensagem(string body, string codigo, string mensagem)
        {
            var handler = new SequencedFakeHttpMessageHandler((HttpStatusCode.UnprocessableEntity, body));
            var erro = await Assert.ThrowsAsync<BancoApiException>(() => Conectar(handler).EnviarAgendamentoAsync(Pix()));
            Assert.Equal(422, erro.HttpStatusCode);
            Assert.Equal(codigo, erro.Mensagens[0].Codigo);
            Assert.Contains(mensagem, erro.Message);
        }

        [Fact]
        public async Task Token_401_RenovaUmaVezEPreservaIdentidade()
        {
            var handler = new SequencedFakeHttpMessageHandler((HttpStatusCode.Unauthorized, "{}"), (HttpStatusCode.OK, Incluido));
            var token = new FakeOAuthTokenProvider();
            await Conectar(handler, tokens: token).EnviarAgendamentoAsync(Pix(), "fixo");
            Assert.Equal(2, handler.Requests.Count);
            Assert.Equal(handler.Bodies[0], handler.Bodies[1]);
            Assert.Equal("fixo", handler.Requests[1].Headers.GetValues("idempotency-key").Single());
            Assert.NotEqual(handler.Requests[0].Headers.GetValues("x-itau-correlationid").Single(), handler.Requests[1].Headers.GetValues("x-itau-correlationid").Single());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Extrato_PercorreLinksEMapeiaContratoExistente(bool lista)
        {
            var handler = new SequencedFakeHttpMessageHandler(
                (HttpStatusCode.OK, Extrato(1, "/statements/" + StatementId + "?page=2", lista)),
                (HttpStatusCode.OK, Extrato(2, lista: lista, operacao: "C")));
            var r = await Conectar(handler).ConsultarExtratoAsync(123456, 10, 2026, 1, 2);
            Assert.Equal(2, r.Count);
            Assert.Equal("account-statement.api.itau.com", handler.Requests[0].RequestUri!.Host);
            Assert.Contains("start_date=2026-10-01", handler.Requests[0].RequestUri!.Query);
            Assert.Contains("end_date=2026-10-02", handler.Requests[0].RequestUri!.Query);
            Assert.Contains("page_size=8000", handler.Requests[0].RequestUri!.Query);
            var item = Assert.IsType<MovimentoItemExtratoTransacao>(r[0].MovimentoItem);
            Assert.Equal(25.50m, item.Valor);
            Assert.Equal(BancoBrTipoCreditoDebitoEnum.Debito, item.Tipo); // reversal não inverte operation.
            Assert.Equal(new DateTime(2026, 10, 1), item.Data);
            Assert.Equal("PIX FICTICIO", item.Descricao);
            Assert.Equal("***.111.111-**", item.CpfCnpj);
            Assert.Equal(BancoBrTipoCreditoDebitoEnum.Credito, Assert.IsType<MovimentoItemExtratoTransacao>(r[1].MovimentoItem).Tipo);
            Assert.EndsWith("/statements/" + StatementId, handler.Requests[1].RequestUri!.AbsolutePath);
        }

        [Theory]
        [InlineData("https://outro.example/statements/150001234567?page=2")]
        [InlineData("/statements/150000000001?page=2")]
        [InlineData("/statements/150001234567?page=1")]
        public async Task Extrato_LinkInvalidoOuCiclo_NaoRetornaListaParcial(string next)
        {
            var handler = new SequencedFakeHttpMessageHandler((HttpStatusCode.OK, Extrato(1, next)), (HttpStatusCode.OK, Extrato(1, next)));
            await Assert.ThrowsAsync<JsonSerializationException>(() => Conectar(handler).ConsultarExtratoAsync(123456, 10, 2026));
            Assert.InRange(handler.Requests.Count, 1, 2);
        }

        [Fact]
        public async Task Saldo_SelecionaContaConfiguradaSemSomarContasOuAplicacoes()
        {
            var body = new JObject { ["data"] = new JArray(Saldo("999999999999", 999), Saldo(StatementId, 100)) }.ToString();
            var handler = new SequencedFakeHttpMessageHandler((HttpStatusCode.PartialContent, body));
            var saldo = Assert.IsType<MovimentoItemExtratoSaldo>((await Conectar(handler, true).ConsultarSaldoAsync()).MovimentoItem);
            Assert.Equal(100, saldo.SaldoAtual);
            Assert.Equal(10, saldo.SaldoBloqueado);
            Assert.Equal("https://account-statement.api.hom.itau.com/account-statement/v1/balances", handler.Requests[0].RequestUri!.AbsoluteUri);
        }

        [Fact]
        public async Task Saldo_ContaComErroParcial_NaoRetornaZero()
        {
            var body = new JObject { ["data"] = new JArray(Saldo(StatementId, 0, true)) }.ToString();
            var handler = new SequencedFakeHttpMessageHandler((HttpStatusCode.PartialContent, body));
            var erro = await Assert.ThrowsAsync<BancoApiException>(() => Conectar(handler).ConsultarSaldoAsync());
            Assert.Equal(206, erro.HttpStatusCode);
        }

        [Fact]
        public async Task OAuth_ContaHomologacao_UsaEndpointECredenciaisDocumentados()
        {
            var handler = new SequencedFakeHttpMessageHandler((HttpStatusCode.OK, "{\"access_token\":\"ficticio\",\"expires_in\":300}"),
                (HttpStatusCode.OK, new JObject { ["data"] = new JArray(Saldo(StatementId, 10)) }.ToString()));
            var banco = BancoApi.ConectarItau(Config(true), null, null, new HttpClient(handler));
            await banco.ConsultarSaldoAsync();
            Assert.Equal("https://sts.rdhi.com.br/api/oauth/token", handler.Requests[0].RequestUri!.AbsoluteUri);
            Assert.Contains("grant_type=client_credentials", handler.Bodies[0]);
            Assert.Contains("client_secret=segredo-ficticio", handler.Bodies[0]);
        }

        [Fact]
        public async Task OperacoesSemContrato_NaoEnviamHttp()
        {
            var handler = new SequencedFakeHttpMessageHandler();
            var banco = Conectar(handler);
            await Assert.ThrowsAsync<NotSupportedException>(() => banco.ConsultarAgendamentoAsync(Pix()));
            await Assert.ThrowsAsync<NotSupportedException>(() => banco.CancelarAgendamentoAsync(Pix()));
            await Assert.ThrowsAsync<NotSupportedException>(() => banco.ConsultarComprovanteAsync(Pix()));
            var boleto = Pix(); boleto.MovimentoItem = new MovimentoItemPagamentoTituloCodigoBarra();
            await Assert.ThrowsAsync<NotSupportedException>(() => banco.EnviarAgendamentoAsync(boleto));
            await Assert.ThrowsAsync<NotSupportedException>(() => banco.ConsultarDDAAsync(123456, new DateTime(2026, 10, 1), new DateTime(2026, 10, 2), SituacaoBoletoEnum.EmAberto, TipoDataConsultaEnum.Vencimento));
            Assert.Empty(handler.Requests);
        }

        [Fact]
        public async Task ValidacaoAntesDoEnvio_PermiteCorrigirMovimento()
        {
            var handler = new SequencedFakeHttpMessageHandler((HttpStatusCode.OK, Incluido));
            var banco = Conectar(handler); var m = Pix(); m.DataPagamento = default;
            await Assert.ThrowsAsync<ArgumentException>(() => banco.EnviarAgendamentoAsync(m, "fixo"));
            Assert.Empty(handler.Requests);
            m.DataPagamento = new DateTime(2026, 10, 28);
            await banco.EnviarAgendamentoAsync(m, "fixo");
            Assert.Single(handler.Requests);
        }

        [Theory]
        [InlineData("boleto")]
        [InlineData("convenio")]
        [InlineData("ted")]
        public async Task Fachada_ConsultaPagamentoDeOutroCanal_PreservaMesmaFormaDeConsumo(string modalidade)
        {
            var handler = new SequencedFakeHttpMessageHandler((HttpStatusCode.OK, Detalhe()));
            var m = Pix(); m.NumeroDocumentoNoBanco = Id;
            m.MovimentoItem = modalidade == "boleto" ? new MovimentoItemPagamentoTituloCodigoBarra() :
                modalidade == "convenio" ? new MovimentoItemPagamentoConvenioCodigoBarra() : new MovimentoItemTransferenciaTED();
            Assert.Same(m, await Conectar(handler).ConsultarAgendamentoAsync(m));
            Assert.Equal(BancoBrSituacaoEnum.Efetivado, m.Situacao);
            Assert.Single(handler.Requests);
        }

        [Fact]
        public async Task Consulta_DebitoDeOutraConta_NaoAtualizaSituacao()
        {
            var body = JObject.Parse(Detalhe()); body["data"]!["dados_debito"]!["numero_conta_debito"] = "99999999";
            var handler = new SequencedFakeHttpMessageHandler((HttpStatusCode.OK, body.ToString()));
            var m = Pix(); m.NumeroDocumentoNoBanco = Id;
            await Assert.ThrowsAsync<JsonSerializationException>(() => Conectar(handler).ConsultarAgendamentoAsync(m));
            Assert.Null(m.Situacao);
        }

        [Fact]
        public async Task CancelamentoDeToken_NaoEnviaPagamento()
        {
            var handler = new SequencedFakeHttpMessageHandler();
            using var cancelamento = new CancellationTokenSource(); cancelamento.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Conectar(handler).EnviarAgendamentoAsync(Pix(), cancellationToken: cancelamento.Token));
            Assert.Empty(handler.Requests);
        }
    }
}
