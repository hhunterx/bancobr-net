# Cobertura Itaú nas operações do BancoBr

Análise de 29/10/2026 com as especificações oficiais preservadas na [captura do portal](portal-2026-10-28/README.md). O escopo aprovado pelo usuário é a interseção entre o que o projeto já cobre e o que o Itaú disponibiliza nesses contratos. Não inclui acrescentar os produtos de cobrança/recebimentos do catálogo ao domínio atual de pagamentos.

| Chamada existente | Recurso Itaú correspondente | Situação |
| --- | --- | --- |
| `EnviarAgendamentoAsync` — Pix por chave/dados | SISPAG `POST /transferencias` | Implementado; inclusão permanece pendente até a consulta confirmar liquidação |
| `EnviarAgendamentoAsync` — Pix QR Code | Mesmo endpoint, com `emv` ou `url` | Implementado com os campos existentes `QrCode` e `URL_ChavePix` |
| `ConsultarAgendamentoAsync` — Pix, boleto, convênio ou TED existentes | `GET /pagamentos_sispag/{id_pagamento_sispag}` | Implementado; valida conta e identidade e preenche dados gerais e específicos disponíveis |
| Consulta prévia de boleto/chave Pix | Não presente no contrato SISPAG | Indisponível; consultar pagamento existente exige seu id bancário |
| `EnviarAgendamentoAsync` — boleto, convênio ou TED | Nenhum endpoint de inclusão correspondente nos contratos salvos | Indisponível por esta integração API; não confundir emissão de cobrança com pagamento de fornecedor |
| `CancelarAgendamentoAsync` | Nenhum endpoint de cancelamento de pagamento SISPAG nos contratos salvos | Indisponível; baixa de boleto de cobrança e devolução de Pix recebido têm outro significado |
| `ConsultarDDAAsync` | Nenhum contrato de DDA no catálogo liberado | Indisponível; consultar carteira de boletos emitidos não equivale a DDA do pagador |
| `ConsultarExtratoAsync` | Account Statement `GET /statements/{statementId}` | Implementado para conta corrente e período, com paginação |
| `ConsultarSaldoAsync` | Account Statement `GET /balances` | Implementado; seleciona a conta configurada e trata erro parcial |
| `ConsultarComprovanteAsync` | Nenhum endpoint de PDF de comprovante de pagamento nos contratos salvos | Indisponível; segunda via de boleto emitido não é comprovante de quitação |
| `ObterOuCriarWorkspaceAsync` | Sem equivalente necessário no Itaú | Recurso específico do fluxo Santander; indisponível no Itaú |

## Evidência e distinções

O [OpenAPI SISPAG 1.14.30](portal-2026-10-28/originais/itau-ep9-gtw-sispag-ext.openapi.json) oferece três caminhos: inclusão de Pix, lista de pagamentos e detalhe de pagamento. A [visão geral](portal-2026-10-28/paginas/itau-ep9-gtw-sispag-ext.visao-geral.txt) explicita a inclusão de transferências e QR Code Pix; não apresenta inclusão de boleto ou TED. As [regras adicionais](portal-2026-10-28/paginas/itau-ep9-gtw-sispag-ext.informacoes-adicionais.txt) detalham formatos, limites e respostas. O endpoint de lista é conhecido, mas não se criou uma nova chamada pública só para expô-lo.

O [OpenAPI Account Statement 1.33.0](portal-2026-10-28/originais/itau-vw9-api-account-statement-v1-externo.openapi.json) cobre o extrato e o saldo usados na fachada. Rendimentos, ordens judiciais, posições de investimento e contas a receber têm contratos próprios e não são transformados em pagamentos ou somados a um saldo sem equivalência documentada.

O [OpenAPI Boletos 1.18.0](portal-2026-10-28/originais/itau-ep9-api-boleto-v1-externo.openapi.json) inclui `GET /documentos` e `GET /documentos/{documentoId}` para segunda via de documento do título. Esse PDF não demonstra quitação; não pode preencher `ArquivoComprovantePDF` como se fosse comprovante de pagamento. Pelo mesmo motivo, emissão/baixa de cobranças e consulta de ativos financeiros não implementam envio/cancelamento de pagamento nem DDA.

A API de conciliação Pix permite consultar lançamentos na visão pagadora/emissora, mas isso não adiciona inclusão de pagamentos de outras modalidades ou PDF. O extrato da conta e a consulta de pagamento SISPAG já atendem às operações correspondentes da fachada. Não foram introduzidos métodos, modelos, enums ou DTOs novos para expor recursos fora desse escopo.

A ausência de um recurso nos contratos liberados não prova que o Itaú não o ofereça por outro produto ou contratação. Para ampliar a cobertura de pagamentos de saída, é necessária a especificação oficial adicional. O projeto já dispõe de integração Itaú por CNAB 240 para os fluxos de arquivo descritos no README; não se substituiu silenciosamente uma chamada HTTP por remessa CNAB.

## Validação

Os testes locais exercitam envio Pix, QR Code por EMV e URL, identidade entre tentativas, consultas e seus detalhes, respostas parciais, paginação, erros e cancelamento de execução. Todas as respostas usam dados fictícios. Nenhuma comunicação bancária ou homologação foi executada.
