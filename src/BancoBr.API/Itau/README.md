# Usar o Itaú com BancoBr.NET

A integração usa a **API Cash Management (SISPAG) 1.14.30** e a **API Account Statement 1.33.0**, consultadas no portal oficial em 28/10/2026. Foi validada com HTTP simulado; comunicação e homologação bancárias ainda não foram executadas.

## Operações

| Operação | Suporte |
| --- | --- |
| Pix por chave | Incluir e consultar |
| Pix por dados bancários | Incluir com `Ispb`, agência, conta/DV, tipo de conta e favorecido; consultar |
| Pix Copia e Cola ou URL de QR Code | Incluir com `QrCode` EMV ou `URL_ChavePix` e valor final; consultar |
| Boleto, convênio e TED | Consultar pagamento já incluído por outro canal, pelo id SISPAG, preenchendo os detalhes disponíveis |
| Extrato de conta corrente | Consultar período com paginação; retorna `MovimentoItemExtratoTransacao` |
| Saldo de conta corrente | Consultar a conta configurada; retorna `MovimentoItemExtratoSaldo` |
| Incluir boleto, convênio ou TED; cancelar; DDA; comprovante PDF | Sem suporte nos contratos consultados; a chamada lança `NotSupportedException` |

O catálogo possui APIs de emissão de boletos e de recebimento de Pix. Elas não representam pagamento de boleto de fornecedor nem transferência de saída. Esta implementação usa conta pagadora **corrente** e módulo SISPAG **Fornecedores**. Não há novos modelos, DTOs, enums ou métodos públicos.

## Acesso e conexão

Solicite ao Itaú a habilitação de SISPAG e Account Statement para a aplicação. Confirme se a mesma credencial está habilitada para ambos; se os produtos tiverem credenciais diferentes, use conexões separadas para as respectivas operações. Emissão, aprovação e renovação do certificado dinâmico são feitas fora da biblioteca. Reúna certificado e chave privada em PFX para usar `CertificateSource`.

```csharp
using BancoBr.API;
using BancoBr.API.Base.Models;
using BancoBr.API.Core.Http;
using BancoBr.Common.Enums;
using BancoBr.Common.Instances;

// Credenciais e certificado vêm da configuração segura da aplicação.
var banco = BancoApi.Conectar(new ConfiguracaoAPI
{
    ClientId = clientId,
    ClientSecret = clientSecret,
    CertificadoDigital = CertificateSource.FromPfxFile(caminhoPfx, senhaPfx),
    Correntista = new Correntista
    {
        Banco = (int)BancoEnum.Itau,
        NumeroAgencia = 1500,
        NumeroConta = 123456,
        DVConta = "7",
        TipoPessoa = TipoInscricaoCPFCNPJEnum.CNPJ,
        CPF_CNPJ = "11.111.111/0001-11" // fictício
    }
});
```

O fluxo padrão usa `client_credentials` com Client ID/Secret no corpo e mTLS. A documentação adicional do SISPAG também descreve credenciais com `private_key_jwt`. Para uma credencial desse tipo, forneça o emissor de token da aplicação na sobrecarga existente `BancoApi.Conectar(configuracao, tokenProvider)` com `IAccessTokenProvider`; a biblioteca não emite JWT. Confirme o fluxo liberado pelo Itaú antes da homologação. O `x-itau-apikey` usa o `ClientId`, e cada requisição recebe uma correlação própria.

| Ambiente | SISPAG | Account Statement |
| --- | --- | --- |
| Produção | `https://api.gateway.itau.com.br/sispag/v1/` | `https://account-statement.api.itau.com/account-statement/v1/` |
| `AmbienteHomologacao = true` | Sandbox do portal: `https://sandbox.devportal.itau.com.br/itau-ep9-gtw-sispag-ext/v1/` | Homologação: `https://account-statement.api.hom.itau.com/account-statement/v1/` |

Os ambientes de teste dos dois produtos são distintos. O token de produção usa `https://sts.itau.com.br/api/oauth/token`; Account Statement em homologação usa `https://sts.rdhi.com.br/api/oauth/token`. A documentação adicional SISPAG indica `https://api.itau.com.br/sandbox/api/oauth/token` para autenticação no sandbox; a emissão de tokens nesse ambiente precisa ser confirmada com a credencial liberada. Um token externo já emitido pode ser fornecido pelo mesmo `IAccessTokenProvider`. O endpoint interno `oauthd.itau` presente no OpenAPI não é utilizado. Nenhuma dessas alternativas executa uma chamada durante `Conectar`.

## Enviar e acompanhar Pix

```csharp
var movimento = new Movimento
{
    NumeroDocumento = "ERP-0001", // referencia_empresa: até 20 posições
    DataPagamento = dataPagamento,
    ValorPagamento = 123.40m,
    MovimentoItem = new MovimentoItemTransferenciaPIX
    {
        ChavePIX = "fornecedor@example.com"
    }
};

// Gere e persista esta chave antes do primeiro envio.
// Reutilize a mesma chave e os mesmos dados em toda tentativa do mesmo pagamento.
await banco.EnviarAgendamentoAsync(movimento, idempotencyKey: chavePersistida);
await banco.ConsultarAgendamentoAsync(movimento);
```

O envio direto não faz resolução prévia de chave. `Sucesso` e `Sucesso (pre-autorizado)` significam inclusão aceita e deixam `Situacao = Agendado`; somente a consulta confirma `Efetivado`. Guarde `NumeroDocumentoNoBanco`, preenchido com `cod_pagamento`, para as consultas seguintes. Com esse id preenchido, um novo envio apenas consulta o pagamento existente.

A chave de idempotência aceita até 255 caracteres ASCII imprimíveis. Quando omitida, a biblioteca usa `NumeroDocumento`; ele deve ser único por pagamento. Não gere uma chave nova após timeout. A biblioteca conserva corpo/chave nas tentativas do mesmo objeto e rejeita alteração dos dados antes de reenviá-lo. Após reiniciar a aplicação ou recriar a conexão, a preservação depende da chave e dos dados salvos pelo consumidor. Não há recuperação automática por idempotência nesta integração.

Para Pix por dados, use `MovimentoItemTransferenciaPIX` com `Ispb` de oito dígitos, agência, conta, DV e `TipoConta`, e preencha `Movimento.Favorecido` com CPF/CNPJ e tipo de pessoa. O código COMPE em `Banco` sozinho não substitui o ISPB. Para QR Code, use `MovimentoItemPagamentoTituloPIXQRCode.QrCode` e o valor final em `ValorPagamento`. Como alternativa ao EMV, informe a URL do QR Code em `URL_ChavePix` (até 77 posições, HTTPS, com ou sem o prefixo `https://`, conforme os exemplos do Itaú). A biblioteca envia a URL ao banco, sem acessá-la. Se os dois campos estiverem preenchidos, usa o EMV. `PagarComJurosMulta = true` não é suportado: a biblioteca não decide nem calcula encargos do QR Code.

Para consultar boleto, convênio ou TED de outro canal, use o respectivo `MovimentoItem` existente, preencha `NumeroDocumentoNoBanco` com o UUID SISPAG e chame `ConsultarAgendamentoAsync`. A resposta deve pertencer à conta configurada. Os estados documentados são normalizados; estado desconhecido fica `NaoIntegrado`. Códigos e motivos de erros HTTP ficam em `BancoApiException.Mensagens`.

A consulta preenche valor, data de pagamento, referência da empresa e favorecido quando retornados. Para boleto, também preenche linha digitável, valor nominal, desconto, vencimento e dados do pagador. A linha digitável fica em `NumeroLinhaDigitavel`, sem substituir o código de barras bruto. Para convênio, recupera o código da concessionária, tributo, DAS ou FGTS quando presente. Para QR Code, preenche valor original, encargos, desconto, vencimento e TXID; para TED, situação, histórico e identificação do débito. Campos ausentes preservam os dados já conhecidos. Documentos mascarados permanecem mascarados. A situação consultada e o id SISPAG não são substituídos por números de comprovante, TED ou agendamento de outro protocolo.

O escopo corresponde às operações já oferecidas pelo BancoBr e com suporte nos contratos Itaú consultados. A [matriz por método público](../../../docs/itau/cobertura-bancobr.md) registra a cobertura e os impedimentos. APIs de cobrança, recorrências, investimentos e Saque/Troco não foram adicionadas ao domínio de pagamentos.

## Extrato e saldo

```csharp
var extrato = await banco.ConsultarExtratoAsync(
    numeroContaCorrente: 123456, mes: 10, ano: 2026,
    diaInicial: 1, diaFinal: 15);

var movimentoSaldo = await banco.ConsultarSaldoAsync();
var saldo = (MovimentoItemExtratoSaldo)movimentoSaldo.MovimentoItem;
Console.WriteLine(saldo.SaldoAtual);
```

O extrato usa a agência/conta/DV configurados; o número passado deve coincidir com essa conta. Consulta conta corrente, sem agrupamento CNAB e sem lançamentos pendentes. A lista contém as transações, sem item inicial de saldo. `Valor` é positivo e `Tipo` identifica crédito/débito. `Data` usa a data contábil, com a data do evento como alternativa; `NumeroDocumento` usa o identificador de origem. Documentos mascarados permanecem mascarados.

A paginação segue `pagination.links.next` com páginas de até 8000 itens. Links de outro host/API/conta, repetição ou sequência divergente causam erro, sem devolver uma lista parcial como completa. O cliente aceita os dois formatos documentados de `data`: objeto no OpenAPI e lista no guia adicional.

`ConsultarSaldoAsync` seleciona pelo `statementId` da conta configurada, nunca pelo primeiro item ou pelo resumo de todas as contas da credencial. Mapeia `saldo_total` para `SaldoAtual` e `saldo_bloqueado` para `SaldoBloqueado`. Não soma aplicações automáticas, saldo disponível, limite ou saldo anterior. Os demais campos do modelo mantêm seus valores padrão, sem informação correspondente mapeada. Se a conta configurada tiver erro em uma resposta parcial HTTP 206, lança exceção em vez de retornar saldo zero.

## Fontes e validação

- [Cash Management: referência oficial e OpenAPI 1.14.30](https://devportal.itau.com.br/nossas-apis/itau-ep9-gtw-sispag-ext?tab=especificacaoTecnica).
- [Cash Management: regras adicionais](https://devportal.itau.com.br/nossas-apis/itau-ep9-gtw-sispag-ext?tab=informacoesAdicionais).
- [Account Statement: referência oficial e OpenAPI 1.33.0](https://devportal.itau.com.br/nossas-apis/itau-vw9-api-account-statement-v1-externo?tab=especificacaoTecnica).
- [Account Statement: ambientes, OAuth, paginação e respostas parciais](https://devportal.itau.com.br/nossas-apis/itau-vw9-api-account-statement-v1-externo?tab=informacoesAdicionais).

Os documentos exigem sessão autenticada no portal. Os testes em `tests/BancoBr.Tests/Itau` usam dados fictícios e HTTP simulado, inclusive nas chamadas pela fachada. Não substituem a homologação da credencial, certificado e regras contratadas.
