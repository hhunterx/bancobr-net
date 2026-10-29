# Itaú por API: evidências e implementação

Pesquisa atualizada em 28/10/2026. Branch `codex/itau-api`, criado a partir de `origin/codex/sicredi-api`, commit `72a7f87bb2b90384832cbe530072800d8ee75f1c`.

## Acesso às fontes

**Arquivo local completo:** a sessão foi usada também para capturar as 15 APIs disponíveis no catálogo, seus OpenAPI/Postman, páginas complementares, guias e FAQs. Consulte o [índice do arquivo](itau/portal-2026-10-28/README.md) e o pacote ZIP indicado nele. A captura integral permite consultar os documentos sem depender de nova sessão; os originais ficam locais, fora do versionamento Git. O inventário registra versões, fontes, operações e hashes.

A pesquisa pública inicial encontrou os guias de autenticação e o [Guia de uso Pix](https://www.itau.com.br/media/dam/m/53743b305ecd3bd7/original/Guia-de-Uso-Pix_LP.pdf), mas não os contratos técnicos completos. O [guia do portal](https://devportal.itau.com.br/conheca-o-portal) explica que a referência e os downloads OpenAPI/Postman ficam na área autenticada. Posteriormente, o usuário abriu uma sessão autenticada e autorizou sua consulta. Com isso, o impedimento de acesso às especificações abaixo foi resolvido.

| Produto e versão | Fonte oficial autenticada | Evidência obtida |
| --- | --- | --- |
| Cash Management 1.14.30 | [Referência SISPAG](https://devportal.itau.com.br/nossas-apis/itau-ep9-gtw-sispag-ext?tab=especificacaoTecnica) | OpenAPI baixado pelo controle oficial do portal |
| Account Statement 1.33.0 | [Referência de extrato](https://devportal.itau.com.br/nossas-apis/itau-vw9-api-account-statement-v1-externo?tab=especificacaoTecnica) | OpenAPI baixado pelo controle oficial do portal |

SHA-256 dos arquivos baixados, sem incorporar os exemplos de terceiros aos testes ou ao repositório:

- Cash Management: `9b9fc2cc3ee73a44af70feeeb4e961b4d32f729882e53c1b3e9015b3d5acf6c6`.
- Account Statement: `09db755cfd83ee76af007757b7b2de5168a9f374887807112ef5d330d8d3723c`.

## Conclusões por operação

A [visão geral e referência SISPAG](https://devportal.itau.com.br/nossas-apis/itau-ep9-gtw-sispag-ext) documentam inclusão exclusiva de Pix em `POST /transferencias` e consulta das modalidades de pagamento em `GET /pagamentos_sispag` e `GET /pagamentos_sispag/{id_pagamento_sispag}`. O catálogo de emissão de boletos trata recebimentos, não substitui pagamento de boleto de fornecedor.

A [referência Account Statement](https://devportal.itau.com.br/nossas-apis/itau-vw9-api-account-statement-v1-externo) cobre extrato `GET /statements/{statementId}` e saldo `GET /balances`, além de produtos de investimento/rendimentos/ordens judiciais fora do escopo da fachada atual.

| Recurso | Implementação nesta alteração |
| --- | --- |
| Pix chave, dados com ISPB e QR Code EMV | Envio direto e consulta por id SISPAG |
| Boleto, convênio e TED | Consulta por id, para pagamentos incluídos por outro canal |
| Extrato e saldo | Consulta da conta corrente configurada |
| Inclusão de boleto/convênio/TED, cancelamento, DDA, PDF | Indisponíveis nos contratos consultados; não inferidos por analogia |
| Lista geral de pagamentos SISPAG | Endpoint conhecido, sem criar método público alternativo para expô-lo |

A ausência desses endpoints nos dois contratos não prova que nenhum outro produto do banco os ofereça. Expansão depende da especificação correspondente.

## Regras de implementação verificadas

As [regras adicionais SISPAG](https://devportal.itau.com.br/nossas-apis/itau-ep9-gtw-sispag-ext?tab=informacoesAdicionais) definem conta com DV, módulo Fornecedores, valor/data, campos opcionais e chave de idempotência ASCII de até 255 posições. A implementação usa os modelos existentes, exige identidade persistível e preserva corpo/chave entre tentativas. Inclusão aceita permanece Agendado; somente a consulta confirma liquidação. Não há resolução DICT ou autorização separada por API nesse contrato. As respostas de erro compactas, por campo e por motivo são normalizadas em BancoApiException/MensagemErro.

O schema de consulta descreve objeto direto, enquanto os exemplos do OpenAPI usam envelope `value.data` e o guia usa `data`. A implementação aceita essas formas documentadas e verifica id e conta de débito antes de atualizar a situação. Não trata comprovante numérico de um exemplo como URL ou PDF.

As [regras adicionais Account Statement](https://devportal.itau.com.br/nossas-apis/itau-vw9-api-account-statement-v1-externo?tab=informacoesAdicionais) fornecem hosts produtivo/homologação, construção do statementId (agência 4 + conta 7 + DAC 1), OAuth, paginação e respostas parciais. O host de desenvolvimento aparece primeiro no OpenAPI; a implementação seleciona explicitamente produção/homologação. O schema traz `data` como objeto; o guia também mostra lista. Ambos são aceitos. O extrato retorna transações, sem pendentes ou saldo de período. SaldoAtual recebe saldo_total e SaldoBloqueado recebe saldo_bloqueado, da conta selecionada; não soma contas ou investimentos.

## Autenticação e divergências documentais

A API de extrato documenta Client ID/Secret no corpo e certificado mTLS, com token em `https://sts.itau.com.br/api/oauth/token` ou `https://sts.rdhi.com.br/api/oauth/token` na homologação. A implementação reutiliza OAuthTokenProvider, CertificateSource e IAccessTokenProvider existentes.

SISPAG informa STS nas regras adicionais, mas o OpenAPI contém `oauthd.itau`, endereço interno não utilizado. O guia SISPAG ainda descreve private_key_jwt; o [guia público de autenticação](https://devportal.itau.com.br/autenticacao-documentacao) apresenta alternativas de autenticação. Credenciais que exigem JWT devem usar IAccessTokenProvider externo, sem novo contrato público para chaves. Os fluxos efetivamente liberados, certificados e diferenças entre sandbox SISPAG e homologação Account Statement devem ser confirmados na homologação bancária.

A referência SISPAG mostra o host atual `api.gateway.itau.com.br`, consistente com o [comunicado de certificados e endereços](https://devportal.itau.com.br/certificados-apis-expiracao-2026). Não se transplantaram endpoints de recebimento Pix, Open Finance ou CNAB para esta implementação.

## Encaixe e validação

Os clientes estão em `src/BancoBr.API/Itau`, conectados pelos dois modos existentes de BancoApi.Conectar. BancoEnum.Itau já existia com valor 341. Foram reutilizadas as bases de pagamento/conta corrente, IPagamentoDiretoApi e ISaldoApi. Common, modelos de configuração, enums e CNAB permanecem sem alterações. As diferenças de protocolo são tratadas internamente com Newtonsoft.Json, sem novos DTOs.

Os testes em `tests/BancoBr.Tests/Itau` exercitam a mesma fachada, conta de origem, idempotência, formatos de resposta/erro, renovação no 401, paginação, seleção de saldo e HTTP 206 com dados inteiramente fictícios. O [guia de uso](../src/BancoBr.API/Itau/README.md) registra operações e limitações. Homologação, emissão de credenciais e transações reais não fazem parte desta validação local.
