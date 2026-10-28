# Usar o Bradesco com BancoBr.NET

Esta integração permite pagar boletos, contas de consumo e tributos com código de barras, fazer Pix e TED e consultar saldo e extrato. Você pode usar os métodos que já conhece ou escolher as [chamadas detalhadas](../../../docs/operacoes-api.md) para receber mais informações.

**Situação atual:** autenticação, saldo e extrato passaram em um smoke test no sandbox do Bradesco em 28/10/2026, usando certificado mTLS e os cenários de exemplo fornecidos pelo banco. O extrato retornou um lançamento no período do exemplo. Pagamentos e produção ainda dependem de homologação; os demais fluxos foram validados apenas com respostas simuladas.

## O que está disponível

| Operação | Suporte nesta integração |
| --- | --- |
| Boleto com código de barras ou linha digitável | Consultar os dados, pagar ou agendar, acompanhar e cancelar o agendamento |
| Conta de consumo ou tributo com código de barras | Validar, pagar no dia e acompanhar |
| Pix por chave ou dados bancários | Transferir no dia e acompanhar |
| TED | Transferir no dia, consultar e identificar devoluções |
| Saldo | Consultar o saldo da conta corrente, separado do limite de crédito |
| Extrato | Consultar os lançamentos do período e, nas chamadas detalhadas, o saldo após cada lançamento |
| DDA, pagamento por QR Code, cancelamento de Pix/TED/conta/tributo e PDF de comprovante | Ainda não disponíveis nesta integração |

A API de geração de QR Code do catálogo do Bradesco serve para receber Pix. O pagamento de QR Code depende de outra especificação, ainda não disponível nos documentos consultados.

## Antes de conectar: o que pedir ao Bradesco

Você precisa de uma conta de empresa e de uma aplicação habilitada para os produtos que pretende usar. Solicite as credenciais de testes (**Client ID** e **Client Secret**) e o certificado correspondente à aplicação.

Para usar `CertificateSource`, reúna o certificado e sua chave privada em um arquivo **PFX**. Guarde o arquivo e as senhas fora do repositório.

## Conectar sua aplicação

Os dados da conta abaixo são fictícios. Substitua-os pelos dados liberados pelo Bradesco. As credenciais, o caminho do PFX e a senha devem vir da configuração segura da sua aplicação.

```csharp
using BancoBr.API;
using BancoBr.API.Base.Models;
using BancoBr.API.Core.Http;
using BancoBr.Common.Enums;
using BancoBr.Common.Instances;

var banco = BancoApi.Conectar(new ConfiguracaoAPI
{
    ClientId = clientId,
    ClientSecret = clientSecret,
    CertificadoDigital = CertificateSource.FromPfxFile(caminhoPfx, senhaPfx),
    AmbienteHomologacao = true,
    Correntista = new Correntista
    {
        Banco = (int)BancoEnum.BradescoSA,
        NumeroAgencia = 1234,
        DVAgencia = "0",
        NumeroConta = 12345,
        DVConta = "6",
        Nome = "Empresa fictícia",
        CPF_CNPJ = "12.345.678/0001-00"
    }
});
```

`AmbienteHomologacao = true` seleciona o ambiente de testes. Use as credenciais e o certificado correspondentes a esse ambiente.

## Consultar saldo e extrato

```csharp
var movimentoSaldo = await banco.ConsultarSaldoAsync();
var saldo = (MovimentoItemExtratoSaldo)movimentoSaldo.MovimentoItem;
Console.WriteLine($"Saldo da conta corrente: {saldo.SaldoAtual:C}");

var extrato = await banco.ConsultarExtratoAsync(
    numeroContaCorrente: 12345,
    mes: 10,
    ano: 2026);

foreach (var movimento in extrato)
{
    var item = (MovimentoItemExtratoTransacao)movimento.MovimentoItem;
    Console.WriteLine($"{item.Data:d} [{item.Tipo}] {item.Descricao}: {item.Valor:C}");
}
```

Informe a mesma conta cadastrada na conexão. O extrato inclui os lançamentos do período solicitado; lançamentos futuros não entram nessa lista. O agrupamento de lançamentos por CNAB não está disponível.

## Enviar e acompanhar um boleto

O código de barras, a data, o valor e o número do documento vêm da sua aplicação. Neste exemplo, use um número de documento único para o pagamento:

```csharp
var movimento = new Movimento
{
    NumeroDocumento = "123456",
    DataPagamento = dataPagamento,
    ValorPagamento = valorPagamento,
    MovimentoItem = new MovimentoItemPagamentoTituloCodigoBarra
    {
        CodigoBarras = codigoBarras
    }
};

await banco.EnviarAgendamentoAsync(movimento);
await banco.ConsultarAgendamentoAsync(movimento);

if (movimento.SituacaoBancoBr == BancoBrSituacaoEnum.Efetivado)
{
    Console.WriteLine("Pagamento realizado.");
}
```

A biblioteca valida o boleto e consulta uma prévia antes de enviar o pagamento. Se o valor da prévia for diferente de `ValorPagamento`, o envio é interrompido para que você confira os dados. A mesma proteção vale para contas de consumo e tributos.

**Consulte a situação antes de considerar o pagamento realizado.** A resposta de envio pode indicar que a solicitação ainda está em processamento. Para cancelar um boleto agendado, use `CancelarAgendamentoAsync(movimento)` enquanto o banco permitir a exclusão.

## Preparar os outros pagamentos

Use os mesmos [modelos de movimento dos exemplos gerais](../../../README.md#integração-via-api), observando estas exigências do Bradesco:

| Operação | Dados necessários |
| --- | --- |
| Conta de consumo ou tributo | Código de barras de 44 dígitos, iniciado por 8, data do dia e valor |
| Pix por chave | Nome e CPF/CNPJ do favorecido, tipo de chave, chave, data do dia e valor |
| Pix por dados bancários | Nome e CPF/CNPJ do favorecido, ISPB, agência, conta, dígito, tipo de conta, data do dia e valor |
| TED | Nome e CPF/CNPJ do favorecido, banco, agência, conta corrente, dígito, finalidade, data do dia e valor |

Os valores devem ter até duas casas decimais. Frações de centavo são rejeitadas sem arredondamento. Nesta implementação, Pix, TED, contas e tributos aceitam somente a data do dia; boletos também permitem agendamento.

## Guardar a identidade do pagamento

Salve um `NumeroDocumento` antes do primeiro envio. Use outro número para um novo pagamento e mantenha o mesmo nas tentativas do pagamento original:

| Operação | Limite de `NumeroDocumento` |
| --- | --- |
| Boleto | Número positivo com até 9 dígitos |
| Conta de consumo ou tributo | Até 9 caracteres |
| TED | Até 25 caracteres |
| Pix | Até 35 caracteres |

Depois do envio, guarde também `NumeroDocumentoNoBanco` e a data original do pagamento. Esses dados permitem consultar o pagamento em outra execução da aplicação.

No Pix, se você omitir `NumeroDocumento`, a biblioteca usa `idempotencyKey`. Um UUID é enviado com 32 caracteres, sem hífens. Se ambos forem omitidos, a identidade é gerada e preservada apenas enquanto você reutilizar o mesmo objeto `Movimento`. Para recuperar um envio após reiniciar a aplicação, prefira salvar a identidade antes de enviar.

**Se o envio de uma TED ficar sem resposta, confirme a operação com o banco antes de reenviar.** O manual não garante que o identificador evite uma transferência duplicada.

## Entender a situação e obter o comprovante

Para contas de consumo e tributos, a biblioteca usa a situação informada pelo Bradesco:

| Resposta do banco | Resultado na biblioteca |
| --- | --- |
| Agendado | `Agendado` |
| Debitado | `Efetivado` |
| Saldo insuficiente ou conta encerrada | `Rejeitado`, com o motivo em `DetalheRejeicaoBancoBr` |
| Em processamento | Situação antiga sem confirmação; `EmProcessamento` nas chamadas detalhadas |
| Situação ausente ou desconhecida | Sem confirmação de pagamento; `Desconhecida` nas chamadas detalhadas |

A autenticação é guardada para o comprovante quando o banco a fornece. Ela, sozinha, não confirma que houve débito.

Use `ObterComprovanteAsync` nas [chamadas detalhadas](../../../docs/operacoes-api.md) para receber os dados disponíveis do pagamento. A integração não gera nem baixa um PDF. Também não presume que o pagamento foi autorizado quando o banco não informa essa etapa.

No caso de TED devolvida, o contrato antigo retorna `Rejeitado` e a chamada detalhada retorna `Devolvido`. Outros estados de TED sem confirmação documentada de liquidação permanecem desconhecidos.

## Detalhes técnicos e pontos de homologação

Esta seção reúne os detalhes de protocolo que podem ajudar na homologação ou na análise de uma resposta bancária.

- **Autenticação:** usa `client_credentials`, com credenciais no corpo e certificado mTLS. Pagamentos, saldo e extrato usam `openapi.bradesco.com.br/auth/server-mtls/v2/token`; Pix usa `qrpix.bradesco.com.br/auth/server/oauth/token`. Em homologação, ambos usam `openapisandbox.prebanco.com.br`.
- **Formato do token:** o transporte compartilhado envia formulário. O manual mTLS diverge entre o cabeçalho ilustrado (`application/json`) e a descrição do corpo (`x-www-form-urlencoded`); o envio como formulário foi confirmado no sandbox em 28/10/2026. A validação em produção permanece pendente.
- **Token próprio:** o overload de `Conectar` com `IAccessTokenProvider` aceita um provedor de token. Ele precisa fornecer um token aceito pelo produto chamado. O override de URL OAuth afeta os dois fluxos; prefira os endpoints padrão.
- **Saldo e extrato:** os códigos `00` e `01` indicam sucesso. O saldo da conta corrente vem do produto `998`; ele não inclui o limite de crédito. No sandbox, a biblioteca omite o parâmetro opcional `tipoOperacao`: o teste confirmou HTTP 404 com esse campo e HTTP 200 sem ele, conforme os exemplos oficiais. Em produção, mantém `tipoOperacao=1`.
- **Arrecadação:** `situacaoConta` define o resultado. `01/02` indicam agendamento; `04/06/09`, débito; `05`, saldo insuficiente; `07/08`, processamento; `00`, conta encerrada. A etapa de validação (`tipoRegistro=0`) não confirma pagamento.
- **Boleto:** a resposta de efetivação ilustrada no manual repete dados da prévia. A biblioteca aguarda a consulta de situação para confirmar a liquidação. Na exclusão do agendamento, usa versão 2 conforme a tabela de parâmetros; o exemplo do manual mostra versão 1.
- **Identificadores:** `NumeroDocumentoNoBanco` guarda o `transactionId` de boleto, `idTransacao` de conta/tributo, `idtransacao` de Pix ou `chaveUnicaParaApi` de TED. O Pix é consultado por identidade, sem depender do recebimento do e2e no envio. A consulta de TED usa os sete primeiros caracteres da chave e a data original.
- **Chamadas detalhadas:** as referências podem ser serializadas para consulta posterior. `RetornosBanco` contém as respostas dos recursos bancários, incluindo o e2e do Pix quando fornecido. Requisições, cabeçalhos e tokens OAuth não entram nessa captura. `SituacaoOriginal` preserva o código da situação de arrecadação.

## Fontes oficiais

Manuais baixados publicamente, sem autenticação, a partir do [catálogo Bradesco Developers](https://developers.bradesco.com.br/browseProducts):

- [Pagamento de boletos v1.0.0](https://assets.bradesco/content/dam/portal-bradesco/gestao-api-payments/Manual_API_PGTO_Boleto_Portal_ingles_V1_0_0.pdf).
- [Contas e tributos v1.0.0](https://assets.bradesco/content/dam/portal-bradesco/gestao-api-payments/Manual_API_PGTO_TRIBUTO_INGLES_V1_0_0.pdf).
- [TED v1.0.0](https://assets.bradesco/content/dam/portal-bradesco/gestao-api-payments/Manual_API_TED_V1_0_0_PORTAL_INGLES.pdf).
- [Transferências Pix v1.0.3](https://assets.bradesco/content/dam/portal-bradesco/gestao-api-payments/Manual_Pix_transfer%C3%AAncias_PORTA_V1.0.3_INGLES.pdf).
- [Saldo e extrato v2.2.2](https://assets.bradesco/content/dam/portal-bradesco/gestao-api-payments/Manual_API_Saldos_Extratos_Portal_Ingles_V2.2.2.pdf).
- [Autenticação mTLS v1.0.0](https://assets.bradesco/content/dam/portal-bradesco/gestao-api-payments/Manual_API_DESENVOLVEDOR_Mtls_V1_0_0_INGLES_PORTAL.pdf).
- [Autenticação Pix v2.0.0](https://assets.bradesco/content/dam/portal-bradesco/gestao-api-payments/Manual_API_DESENVOLVEDOR_PIX_Mtls_V2_0_0_INGLES_PORTAL.pdf).

## Teste de comunicação em homologação

O smoke test de 28/10/2026 executou somente autenticação, saldo e extrato pela `BancoApi`, com as credenciais fora do repositório. As consultas e a interpretação das respostas foram aprovadas. Valores e dados bancários foram omitidos dos resultados do teste.

O sandbox usa cenários definidos pelo banco. Na [coleção oficial de saldo e extrato](https://developers.bradesco.com.br/productDetails/8a2e88539549335b01954cccc83e136d/version/2377491905f04d2592cc863bccd38ca1/docs), o exemplo de saldo usa agência `3995` e conta `75557`; o de extrato usa agência `3750`, conta `75557` e o período de **06/11/2024 a 20/11/2024**. Use uma conexão com a agência correspondente a cada exemplo. Outros dados podem retornar HTTP 404 com “Erro de estrutura”, mesmo quando a autenticação funciona.

Esse teste confirma a comunicação e o tratamento desses cenários. Não confirma os fluxos de pagamento nem substitui a homologação para produção.

## Validação local

```sh
dotnet restore BancoBr.sln
dotnet build BancoBr.sln --no-restore
dotnet test tests/BancoBr.Tests/BancoBr.Tests.csproj --no-build --filter FullyQualifiedName~Bradesco
```

Os testes usam dados fictícios e HTTP simulado. Nenhum pagamento ou consulta real faz parte da validação local.
