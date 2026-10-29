using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BancoBr.API.Core.Errors;
using BancoBr.API.Core.OAuth;
using BancoBr.Common.Instances;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BancoBr.API.Bradesco
{
    /// <summary>Transporte das APIs públicas Bradesco, com renovação única no 401 e erros de negócio.</summary>
    internal sealed class BradescoHttpGateway
    {
        private readonly HttpClient _http;
        private readonly IAccessTokenProvider _tokens;
        internal Correntista Origem { get; }
        internal string Raiz { get; }
        internal bool Homologacao { get; }

        internal BradescoHttpGateway(HttpClient http, IAccessTokenProvider tokens, Correntista origem, bool homologacao, bool pix = false)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
            Origem = origem ?? throw new ArgumentNullException(nameof(origem));
            Homologacao = homologacao;
            Raiz = homologacao ? "https://openapisandbox.prebanco.com.br/" : pix ? "https://qrpix.bradesco.com.br/" : "https://openapi.bradesco.com.br/";
        }

        internal static string Digitos(string s) => new string((s ?? "").Where(c => c >= '0' && c <= '9').ToArray());

        internal async Task<JObject> JsonAsync(HttpMethod method, string path, JObject body, CancellationToken ct)
        {
            var json = body?.ToString(Formatting.None);
            for (var tentativa = 0; ; tentativa++)
            {
                using (var request = new HttpRequestMessage(method, Raiz + path))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _tokens.GetAccessTokenAsync(ct).ConfigureAwait(false));
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    if (json != null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                    using (var response = await _http.SendAsync(request, ct).ConfigureAwait(false))
                    {
                        if (response.StatusCode == HttpStatusCode.Unauthorized && tentativa == 0)
                        {
                            _tokens.InvalidateToken();
                            continue;
                        }
                        var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        JObject result;
                        try { result = JObject.Parse(content); }
                        catch (JsonException)
                        {
                            if (response.IsSuccessStatusCode) throw;
                            throw Erro(((int)response.StatusCode).ToString(), response.ReasonPhrase, (int)response.StatusCode);
                        }

                        if (!response.IsSuccessStatusCode)
                            throw Erro((string)result["codigo"] ?? (string)result["code"] ?? (string)result["type"] ?? ((int)response.StatusCode).ToString(),
                                (string)result["detail"] ?? (string)result["mensagem"] ?? (string)result["title"] ?? response.ReasonPhrase, (int)response.StatusCode);
                        Validar(result);
                        return result;
                    }
                }
            }
        }

        /// <summary>Saldo e extrato também aceitam 01: sucesso com dados adicionais.</summary>
        internal static void ValidarConta(JObject r) => Validar(r, aceitarDadosAdicionais: true);

        internal static void Validar(JObject r) => Validar(r, aceitarDadosAdicionais: false);

        private static void Validar(JObject r, bool aceitarDadosAdicionais)
        {
            if (int.TryParse((string)r["status"], out var status) && status >= 400)
                throw Erro((string)r["causa"] ?? status.ToString(), (string)r["mensagem"], status);
            foreach (var nome in new[] { "retorno", "sqlCode", "codigoRetorno", "codigoErro", "codigoDeErro" })
            {
                var valor = (string)r[nome];
                if (aceitarDadosAdicionais && nome == "codigoRetorno" && int.TryParse(valor, out var retorno) && retorno == 1)
                    continue;
                if (!string.IsNullOrWhiteSpace(valor) && valor.Trim('0', ' ') != "")
                    throw Erro(valor, (string)r["mensagem"] ?? (string)r["descricaoRetorno"]);
            }
            if (r.Value<int?>("codigoDeRetorno") is int codigo && codigo != 0 && codigo != 23)
                throw Erro(codigo.ToString(), (string)r["mensagem"]);
        }

        internal static BancoApiException Erro(string codigo, string mensagem, int status = 200)
            => new BancoApiException(status, new[] { new MensagemErro { Codigo = codigo, Mensagem = mensagem ?? "Falha de negócio Bradesco." } });
    }
}
