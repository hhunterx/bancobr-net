using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BancoBr.Common.Instances;
using Newtonsoft.Json.Linq;

namespace BancoBr.API.Bradesco
{
    /// <summary>
    /// Emite os comprovantes de boleto e arrecadação genérica (02/03) conforme os manuais Bradesco.
    /// O PDF é composto localmente; dados e autenticação vêm da operação bancária.
    /// </summary>
    internal static class ComprovantePdf
    {
        private static readonly CultureInfo Cultura = CultureInfo.GetCultureInfo("pt-BR");

        internal static byte[] Gerar(Movimento movimento, Correntista origem, JObject dados)
        {
            var linhas = new List<string> { "BRADESCO", "Comprovante de pagamento", "" };
            if (movimento.MovimentoItem is MovimentoItemPagamentoTituloCodigoBarra boleto)
            {
                var autenticacao = boleto.NumeroAutenticacaoPagamento;
                var obrigatorios = new[] { "nomeInstituicaoRecebedora", "digitavel1", "digitavel2", "nomeBancoDestino", "icdentTitloDda", "ccnpjCdentCobr", "isacdo", "ccgcCpfSacdo", "cnpjCpfCtoPgto", "dataVencimento", "valorTituloOritem", "valorPagamento", "horaTransacao", "valorJurosTitulo", "valorMultaTitulo", "valorDescontoTitulo", "vabtmtTitloCalcd", "valorBonusTitulo" };
                if (string.IsNullOrWhiteSpace(autenticacao) || autenticacao == "0" || !Completos(dados, obrigatorios) || movimento.DataPagamento == default) return null;
                linhas.AddRange(new[]
                {
                    "Pagamento de boleto", "Banco recebedor: " + dados["nomeInstituicaoRecebedora"],
                    "Linha digitável: " + dados["digitavel1"] + " " + dados["digitavel2"],
                    "Instituição emissora: " + dados["nomeBancoDestino"],
                    "Beneficiário: " + dados["icdentTitloDda"],
                    "Nome fantasia: " + dados["nomeFantasiaBeneficiario"],
                    "CPF/CNPJ beneficiário: " + dados["ccnpjCdentCobr"],
                    "Sacador/avalista: " + dados["isacdrAvalsOutro"],
                    "CPF/CNPJ sacador/avalista: " + dados["ccnpjSacdrAvals"],
                    "Pagador: " + dados["isacdo"], "CPF/CNPJ pagador: " + dados["ccgcCpfSacdo"],
                    "Pagador final: " + origem.Nome, "CPF/CNPJ pagador final: " + dados["cnpjCpfCtoPgto"],
                    "Vencimento: " + Data(dados["dataVencimento"]),
                    "Valor nominal: " + Moeda(dados["valorTituloOritem"]),
                    "Encargos: " + (dados.Value<decimal>("valorJurosTitulo") + dados.Value<decimal>("valorMultaTitulo")).ToString("C2", Cultura),
                    "Descontos/abatimentos/bônus: " + (dados.Value<decimal>("valorDescontoTitulo") + dados.Value<decimal>("vabtmtTitloCalcd") + dados.Value<decimal>("valorBonusTitulo")).ToString("C2", Cultura),
                    "Valor pago: " + Moeda(dados["valorPagamento"]),
                    "Data do pagamento: " + movimento.DataPagamento.ToString("dd/MM/yyyy", Cultura),
                    "Hora da transação: " + DateTime.ParseExact(dados.Value<int>("horaTransacao").ToString("D6", CultureInfo.InvariantCulture), "HHmmss", CultureInfo.InvariantCulture).ToString("HH:mm:ss", Cultura),
                    "Autenticação bancária: " + autenticacao
                });
            }
            else
            {
                var tipo = dados.Value<int?>("tipoComprovante");
                if (tipo != 2 && tipo != 3) throw new NotSupportedException("Modelo de comprovante Bradesco não implementado: " + tipo + ". Suportados: 02 e 03.");
                var obrigatorios = new[] { "codigoBarras", "dataDebito", "valorDebito", "autenticacaoBancaria", "nomeFantasia", "identificacaoPagamento", "numeroIdentidade", "identidadeDocumento" };
                if (!Completos(dados, obrigatorios) || dados.Value<decimal>("valorDebito") <= 0 || (string)dados["autenticacaoBancaria"] == "0") return null;
                linhas.AddRange(new[]
                {
                    tipo == 3 ? "Pagamento de conta de consumo" : "Pagamento de tributo",
                    "Agência: " + origem.NumeroAgencia, "Conta: " + origem.NumeroConta + "-" + origem.DVConta,
                    "Código de barras: " + dados["codigoBarras"],
                    "Empresa/órgão: " + dados["nomeFantasia"],
                    "Descrição: " + dados["identificacaoPagamento"],
                    (string)dados["numeroIdentidade"] + ": " + dados["identidadeDocumento"],
                    "Data do pagamento: " + Data(dados["dataDebito"]),
                    "Valor pago: " + Moeda(dados["valorDebito"])
                });
                if (tipo == 2)
                {
                    if (!Completos(dados, new[] { "dataVencimentoOrgao", "valorTributo", "multa", "juros", "descontos" })) return null;
                    linhas.Add("Vencimento: " + Data(dados["dataVencimentoOrgao"]));
                    linhas.Add("Juros: " + Moeda(dados["juros"]));
                    linhas.Add("Multa: " + Moeda(dados["multa"]));
                    linhas.Add("Desconto: " + Moeda(dados["descontos"]));
                    linhas.Add("Valor principal: " + Moeda(dados["valorTributo"]));
                }
                linhas.Add("Autenticação bancária: " + dados["autenticacaoBancaria"]);
            }
            linhas.Add("");
            linhas.Add("Documento gerado por BancoBr.NET com dados da API Bradesco.");
            return Escrever(linhas);
        }

        private static bool Completos(JObject dados, IEnumerable<string> campos)
            => campos.All(c => !string.IsNullOrWhiteSpace((string)dados[c]));
        private static string Moeda(JToken valor) => valor.Value<decimal>().ToString("C2", Cultura);
        private static string Data(JToken valor)
            => DateTime.ParseExact((string)valor, new[] { "yyyyMMdd", "yyyy-MM-dd" }, CultureInfo.InvariantCulture, DateTimeStyles.None).ToString("dd/MM/yyyy", Cultura);

        // PDF 1.4 com fonte padrão e codificação WinAnsi: dispensa fontes do sistema e dependências nativas.
        // Texto externo é sempre escapado como hexadecimal, sem comandos PDF interpolados.
        private static byte[] Escrever(IEnumerable<string> linhas)
        {
            var texto = new List<string>();
            foreach (var linha in linhas)
                for (var i = 0; i < Math.Max(1, linha.Length); i += 78)
                    texto.Add(linha.Substring(i, Math.Min(78, linha.Length - i)));
            var quantidade = (texto.Count + 43) / 44;
            var objetos = new List<string>
            {
                "<< /Type /Catalog /Pages 2 0 R >>",
                "<< /Type /Pages /Kids [" + string.Join(" ", Enumerable.Range(0, quantidade).Select(i => (4 + i * 2) + " 0 R")) + "] /Count " + quantidade + " >>",
                "<< /Type /Font /Subtype /Type1 /BaseFont /Courier /Encoding /WinAnsiEncoding >>"
            };
            for (var pagina = 0; pagina < quantidade; pagina++)
            {
                var conteudo = new StringBuilder("BT /F1 10 Tf 48 795 Td 16 TL\n");
                foreach (var linha in texto.Skip(pagina * 44).Take(44))
                {
                    var bytes = linha.Select(c => c >= 32 && c <= 255 ? (byte)c : (byte)'?').ToArray();
                    conteudo.Append('<').Append(BitConverter.ToString(bytes).Replace("-", "")).Append("> Tj T*\n");
                }
                conteudo.Append("ET\n");
                objetos.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 3 0 R >> >> /Contents " + (5 + pagina * 2) + " 0 R >>");
                objetos.Add("<< /Length " + Encoding.ASCII.GetByteCount(conteudo.ToString()) + " >>\nstream\n" + conteudo + "endstream");
            }
            using (var arquivo = new MemoryStream())
            {
                Action<string> gravar = valor => { var bytes = Encoding.ASCII.GetBytes(valor); arquivo.Write(bytes, 0, bytes.Length); };
                gravar("%PDF-1.4\n");
                var offsets = new List<long>();
                for (var i = 0; i < objetos.Count; i++)
                {
                    offsets.Add(arquivo.Position);
                    gravar((i + 1) + " 0 obj\n" + objetos[i] + "\nendobj\n");
                }
                var inicio = arquivo.Position;
                gravar("xref\n0 " + (objetos.Count + 1) + "\n0000000000 65535 f \n");
                foreach (var offset in offsets) gravar(offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
                gravar("trailer\n<< /Size " + (objetos.Count + 1) + " /Root 1 0 R >>\nstartxref\n" + inicio + "\n%%EOF\n");
                return arquivo.ToArray();
            }
        }
    }
}
