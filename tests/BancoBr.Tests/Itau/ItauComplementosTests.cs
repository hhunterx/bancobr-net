using System.Net;
using BancoBr.API;
using BancoBr.API.Base.Models;
using BancoBr.Common.Enums;
using BancoBr.Common.Instances;
using BancoBr.Tests.Sicoob;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BancoBr.Tests.Itau
{
    public class ItauComplementosTests
    {
        private const string Id = "22222222-2222-4222-8222-222222222222";
        private static BancoApi Conectar(SequencedFakeHttpMessageHandler handler) => BancoApi.ConectarItau(new ConfiguracaoAPI
        {
            ClientId = "client-ficticio", Correntista = new Correntista
            {
                Banco = (int)BancoEnum.Itau, NumeroAgencia = 1500, NumeroConta = 123456, DVConta = "7",
                TipoPessoa = TipoInscricaoCPFCNPJEnum.CNPJ, CPF_CNPJ = "11.111.111/0001-11"
            }
        }, null, new FakeOAuthTokenProvider(), new HttpClient(handler));

        private static Movimento Movimento(MovimentoItem item) => new Movimento
        {
            MovimentoItem = item, NumeroDocumento = "ERP-0002", ValorPagamento = 100.50m, DataPagamento = new DateTime(2026, 10, 29)
        };

        private static JObject Detalhe()
        {
            return new JObject
            {
                ["data"] = new JObject
                {
                    ["dados_debito"] = new JObject
                    {
                        ["numero_agencia_debito"] = "1500", ["numero_conta_debito"] = "01234567",
                        ["nome_empresa_debito"] = "PAGADOR FICTICIO", ["cnpj_debito"] = "11111111000111"
                    },
                    ["dados_pagamento"] = new JObject
                    {
                        ["id_pagamento"] = Id, ["status"] = "Efetuado", ["valor_pagamento"] = "200.50",
                        ["data_pagamento"] = "2026-10-28", ["referencia_empresa"] = "ERP-BANCO",
                        ["nome_favorecido"] = "FAVORECIDO FICTICIO", ["cpf_cnpj_favorecido"] = "***.111.111-**"
                    }
                }
            };
        }

        [Theory]
        [InlineData("https://qr.example.com/cobranca/1")]
        [InlineData("qr.example.com/cobranca/1")]
        public async Task Fachada_QrPorUrl_UsaMesmoEnvioEModelo(string url)
        {
            var handler = new SequencedFakeHttpMessageHandler((HttpStatusCode.OK, "{\"status_pagamento\":\"Sucesso\",\"cod_pagamento\":\"" + Id + "\"}"));
            var m = Movimento(new MovimentoItemPagamentoTituloPIXQRCode { URL_ChavePix = url });
            Assert.Same(m, await Conectar(handler).EnviarAgendamentoAsync(m, "qr-estavel"));
            var body = JObject.Parse(handler.Bodies.Single()!);
            Assert.Equal(url, (string?)body["url"]);
            Assert.Null(body["emv"]);
            Assert.Null(body["chave"]);
            Assert.Equal("qr-estavel", handler.Requests.Single().Headers.GetValues("idempotency-key").Single());
            Assert.Equal(BancoBrSituacaoEnum.Agendado, m.Situacao);
        }

        [Theory]
        [InlineData("")]
        [InlineData("http://qr.example.com/1")]
        [InlineData("https://usuario:senha@qr.example.com/1")]
        [InlineData("https://qr.example.com/1#fragmento")]
        [InlineData("fornecedor@example.com")]
        public async Task Qr_UrlInvalida_NaoEnvia(string url)
        {
            var handler = new SequencedFakeHttpMessageHandler();
            await Assert.ThrowsAsync<ArgumentException>(() => Conectar(handler).EnviarAgendamentoAsync(
                Movimento(new MovimentoItemPagamentoTituloPIXQRCode { URL_ChavePix = url })));
            Assert.Empty(handler.Requests);
        }

        [Fact]
        public async Task Qr_EmvTemPrioridade_NaoEnviaIdentificacoesConcorrentes()
        {
            var handler = new SequencedFakeHttpMessageHandler((HttpStatusCode.OK, "{\"status_pagamento\":\"Sucesso\",\"cod_pagamento\":\"" + Id + "\"}"));
            await Conectar(handler).EnviarAgendamentoAsync(Movimento(new MovimentoItemPagamentoTituloPIXQRCode
            {
                QrCode = "000201-EMV-FICTICIO", URL_ChavePix = "https://qr.example.com/1"
            }));
            var body = JObject.Parse(handler.Bodies.Single()!);
            Assert.Equal("000201-EMV-FICTICIO", (string?)body["emv"]);
            Assert.Null(body["url"]);
        }

        [Fact]
        public async Task Consulta_Boleto_PreencheDadosDoTituloSemConfundirLinhaComBarras()
        {
            var body = Detalhe();
            body["data"]!["dados_pagamento"]!["dados_boleto"] = new JObject
            {
                ["linha_digitavel"] = "34191111111111111111111111111111111111111111111",
                ["data_vencimento"] = "2026-10-30", ["valor_boleto"] = "210,50", ["valor_descontos"] = "10.00",
                ["nome_pagador"] = "PAGADOR FICTICIO", ["documento_pagador"] = "11111111000111"
            };
            var handler = new SequencedFakeHttpMessageHandler((HttpStatusCode.OK, body.ToString()));
            var item = new MovimentoItemPagamentoTituloCodigoBarra { CodigoBarras = "codigo-original" };
            var m = Movimento(item); m.NumeroDocumentoNoBanco = Id;
            await Conectar(handler).ConsultarAgendamentoAsync(m);
            Assert.Equal(200.50m, m.ValorPagamento);
            Assert.Equal(new DateTime(2026, 10, 28), m.DataPagamento);
            Assert.Equal("ERP-BANCO", m.NumeroDocumento);
            Assert.Equal("FAVORECIDO FICTICIO", m.Favorecido.Nome);
            Assert.Equal("***.111.111-**", m.Favorecido.CPF_CNPJ);
            Assert.Equal("codigo-original", item.CodigoBarras);
            Assert.Equal("34191111111111111111111111111111111111111111111", item.NumeroLinhaDigitavel);
            Assert.Equal(new DateTime(2026, 10, 30), item.DataVencimento);
            Assert.Equal(210.50m, item.ValorCodigoBarra);
            Assert.Equal(10, item.Desconto);
            Assert.Equal("PAGADOR FICTICIO", item.PagadorConfirmadoNome);
            Assert.Equal("11111111000111", item.PagadorConfirmadoDocumento);
        }

        [Theory]
        [InlineData("dados_concessionaria")]
        [InlineData("dados_tributos_cod_barras")]
        [InlineData("dados_das_cod_barras")]
        [InlineData("dados_fgts")]
        public async Task Consulta_Convenio_RecuperaCodigoDoPagamento(string bloco)
        {
            var body = Detalhe(); body["data"]!["dados_pagamento"]![bloco] = new JObject { ["linha_digitavel"] = new string('8', 48) };
            var handler = new SequencedFakeHttpMessageHandler((HttpStatusCode.OK, body.ToString()));
            var item = new MovimentoItemPagamentoConvenioCodigoBarra();
            var m = Movimento(item); m.NumeroDocumentoNoBanco = Id;
            await Conectar(handler).ConsultarAgendamentoAsync(m);
            Assert.Equal(new string('8', 48), item.CodigoBarra);
            Assert.Equal(200.50m, m.ValorPagamento);
        }

        [Fact]
        public async Task Consulta_Ted_PreencheHistoricoEDebitoSemInventarIdDeAgendamento()
        {
            var body = Detalhe(); body["data"]!["dados_pagamento"]!["dados_ted"] = new JObject
            {
                ["numero_ted"] = "999999", ["complemento_finalidade_ted"] = "HISTORICO FICTICIO"
            };
            var handler = new SequencedFakeHttpMessageHandler((HttpStatusCode.OK, body.ToString()));
            var item = new MovimentoItemTransferenciaTED(); var m = Movimento(item); m.NumeroDocumentoNoBanco = Id;
            await Conectar(handler).ConsultarAgendamentoAsync(m);
            Assert.Equal("HISTORICO FICTICIO", item.Historico);
            Assert.Equal("Efetuado", item.Situacao);
            Assert.Equal("PAGADOR FICTICIO", item.NomePessoaDebito);
            Assert.Equal("11111111000111", item.NumeroCPFCNPJDebito);
            Assert.Equal(Id, m.NumeroDocumentoNoBanco);
            Assert.Equal(0, item.IdAgendamento);
            Assert.Null(item.NumeroControleIF);
        }

        [Fact]
        public async Task Consulta_Qr_PreencheValorOriginalEEncargosDevolvidosPeloBanco()
        {
            var body = Detalhe(); body["data"]!["dados_pagamento"]!["dados_pix_qr_code"] = new JObject
            {
                ["data_vencimento"] = "2026-10-30T12:00:00", ["valor_documento"] = "199.50",
                ["multa"] = "2.00", ["juros"] = "1.00", ["desconto"] = "2.00", ["identificador_qr_code"] = "txid-ficticio"
            };
            var handler = new SequencedFakeHttpMessageHandler((HttpStatusCode.OK, body.ToString()));
            var item = new MovimentoItemPagamentoTituloPIXQRCode(); var m = Movimento(item); m.NumeroDocumentoNoBanco = Id;
            await Conectar(handler).ConsultarAgendamentoAsync(m);
            Assert.Equal(199.50m, item.ValorOriginal);
            Assert.Equal(2m, item.Multa);
            Assert.Equal(1m, item.Juros);
            Assert.Equal(2m, item.Desconto);
            Assert.Equal("txid-ficticio", item.TXID);
            Assert.Equal(new DateTime(2026, 10, 30), item.DataVencimento);
        }

        [Fact]
        public async Task Consulta_Pix_PreservaChaveEInformaTitularConfirmadoMascarado()
        {
            var handler = new SequencedFakeHttpMessageHandler((HttpStatusCode.OK, Detalhe().ToString()));
            var item = new MovimentoItemTransferenciaPIX { ChavePIX = "fornecedor@example.com" };
            var m = Movimento(item); m.NumeroDocumentoNoBanco = Id;
            await Conectar(handler).ConsultarAgendamentoAsync(m);
            Assert.Equal("fornecedor@example.com", item.ChavePIX);
            Assert.Equal("FAVORECIDO FICTICIO", item.TitularConfirmadoNome);
            Assert.Equal("***.111.111-**", item.TitularConfirmadoDocumento);
        }

        [Theory]
        [InlineData("valor_pagamento", "1.234,50")]
        [InlineData("data_pagamento", "28/10/2026")]
        public async Task Consulta_DadosInvalidos_NaoAlteraMovimentoParcialmente(string campo, string valor)
        {
            var body = Detalhe(); body["data"]!["dados_pagamento"]![campo] = valor;
            var handler = new SequencedFakeHttpMessageHandler((HttpStatusCode.OK, body.ToString()));
            var m = Movimento(new MovimentoItemTransferenciaPIX()); m.NumeroDocumentoNoBanco = Id;
            await Assert.ThrowsAsync<JsonSerializationException>(() => Conectar(handler).ConsultarAgendamentoAsync(m));
            Assert.Equal(100.50m, m.ValorPagamento);
            Assert.Equal(new DateTime(2026, 10, 29), m.DataPagamento);
            Assert.Null(m.Situacao);
            Assert.Null(m.Favorecido);
        }
    }
}
