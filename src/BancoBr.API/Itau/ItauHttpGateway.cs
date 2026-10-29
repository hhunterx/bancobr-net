using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BancoBr.API.Core.Errors;
using BancoBr.API.Core.Http;
using BancoBr.API.Core.OAuth;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BancoBr.API.Itau
{
    /// <summary>Transporte Itaú com mTLS, correlação por requisição e renovação no primeiro 401.</summary>
    internal sealed class ItauHttpGateway
    {
        private readonly HttpClient _http;
        private readonly IAccessTokenProvider _tokens;
        private readonly string _clientId;
        internal Uri BaseUri { get; }

        internal ItauHttpGateway(HttpClient http, IAccessTokenProvider tokens, Uri baseUri, string clientId = null)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
            BaseUri = baseUri ?? throw new ArgumentNullException(nameof(baseUri));
            _clientId = clientId;
        }

        internal static HttpClient CriarHttpClient(CertificateSource certificado)
        {
            var handler = new HttpClientHandler { AllowAutoRedirect = false };
            if (certificado != null) handler.ClientCertificates.Add(certificado.GetCertificate());
            return new HttpClient(handler);
        }

        internal Uri Resolver(string caminho)
        {
            // Links de paginação podem ser absolutos, relativos à API ou começar por /statements.
            var uri = caminho.StartsWith("/statements/", StringComparison.Ordinal)
                ? new Uri(BaseUri, caminho.Substring(1)) : new Uri(BaseUri, caminho);
            if (uri.Scheme != BaseUri.Scheme || uri.Authority != BaseUri.Authority ||
                !uri.AbsolutePath.StartsWith(BaseUri.AbsolutePath, StringComparison.Ordinal))
                throw new JsonSerializationException("Link Itaú fora da API consultada.");
            return uri;
        }

        internal async Task<JObject> JsonAsync(HttpMethod method, string caminho, JObject body, CancellationToken ct, string idempotencyKey = null)
        {
            var uri = Resolver(caminho);
            var json = body?.ToString(Formatting.None);
            for (var tentativa = 0; ; tentativa++)
            {
                ct.ThrowIfCancellationRequested();
                using (var request = new HttpRequestMessage(method, uri))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _tokens.GetAccessTokenAsync(ct).ConfigureAwait(false));
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    request.Headers.Add("x-itau-correlationid", Guid.NewGuid().ToString());
                    if (_clientId != null) request.Headers.Add("x-itau-apikey", _clientId);
                    if (idempotencyKey != null) request.Headers.Add("idempotency-key", idempotencyKey);
                    if (json != null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                    using (var response = await _http.SendAsync(request, ct).ConfigureAwait(false))
                    {
                        if (response.StatusCode == HttpStatusCode.Unauthorized && tentativa == 0)
                        {
                            _tokens.InvalidateToken();
                            continue;
                        }
                        var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        JObject root = null;
                        try { root = JObject.Parse(content); }
                        catch (JsonException) when (!response.IsSuccessStatusCode) { }
                        if (!response.IsSuccessStatusCode)
                            throw Erro(root, (int)response.StatusCode, response.ReasonPhrase);
                        return root ?? throw new JsonSerializationException("Resposta Itaú sem objeto JSON.");
                    }
                }
            }
        }

        internal static BancoApiException Erro(JObject root, int status, string mensagem)
        {
            var erros = new List<MensagemErro>();
            foreach (var motivo in root?["motivo_recusa"] as JArray ?? new JArray())
                erros.Add(new MensagemErro { Codigo = (string)motivo["codigo"], Mensagem = (string)motivo["nome"] });
            foreach (var campo in root?["campos"] as JArray ?? new JArray())
                erros.Add(new MensagemErro { Codigo = (string)root["codigo"] ?? status.ToString(CultureInfo.InvariantCulture),
                    Mensagem = (string)campo["campo"] + ": " + (string)campo["mensagem"] });
            if (erros.Count == 0) erros.Add(new MensagemErro
            {
                Codigo = (string)root?["codigo"] ?? (string)root?["status"] ?? status.ToString(CultureInfo.InvariantCulture),
                Mensagem = (string)root?["mensagem"] ?? (string)root?["message"] ?? mensagem
            });
            return new BancoApiException(status, erros);
        }
    }
}
