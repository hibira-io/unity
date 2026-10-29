using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Hibira
{
    /// <summary>Form POSTs for the auth endpoint: UnityWebRequest in Unity, so WebGL builds use fetch.</summary>
    static class HibiraHttp
    {
        public struct Response
        {
            public int Status;
            public string Body;
            /// <summary>Set when no response arrived at all.</summary>
            public string Error;
        }

        public static string Form(IEnumerable<KeyValuePair<string, string>> fields)
        {
            var sb = new StringBuilder();
            foreach (var kv in fields)
            {
                if (sb.Length > 0) sb.Append('&');
                sb.Append(Uri.EscapeDataString(kv.Key)).Append('=').Append(Uri.EscapeDataString(kv.Value ?? ""));
            }
            return sb.ToString();
        }

        public static Task<Response> PostForm(string url, IDictionary<string, string> headers, string form)
        {
            var tcs = new TaskCompletionSource<Response>();
#if UNITY_5_3_OR_NEWER
            var request = new UnityEngine.Networking.UnityWebRequest(url, "POST")
            {
                uploadHandler = new UnityEngine.Networking.UploadHandlerRaw(Encoding.UTF8.GetBytes(form)),
                downloadHandler = new UnityEngine.Networking.DownloadHandlerBuffer(),
            };
            request.SetRequestHeader("Content-Type", "application/x-www-form-urlencoded");
            request.SetRequestHeader("Accept", "application/json");
            foreach (var kv in headers) request.SetRequestHeader(kv.Key, kv.Value);
            request.SendWebRequest().completed += _ =>
            {
                var status = (int)request.responseCode;
                tcs.SetResult(new Response
                {
                    Status = status,
                    Body = request.downloadHandler?.text ?? "",
                    Error = status == 0 ? request.error ?? "no response" : null,
                });
                request.Dispose();
            };
#else
            _ = Send(url, headers, form, tcs);
#endif
            return tcs.Task;
        }

#if !UNITY_5_3_OR_NEWER
        static readonly System.Net.Http.HttpClient Client = new System.Net.Http.HttpClient();

        static async Task Send(string url, IDictionary<string, string> headers, string form, TaskCompletionSource<Response> tcs)
        {
            try
            {
                var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, url)
                {
                    Content = new System.Net.Http.StringContent(form, Encoding.UTF8, "application/x-www-form-urlencoded"),
                };
                request.Headers.TryAddWithoutValidation("Accept", "application/json");
                foreach (var kv in headers) request.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                var response = await Client.SendAsync(request).ConfigureAwait(false);
                tcs.SetResult(new Response { Status = (int)response.StatusCode, Body = await response.Content.ReadAsStringAsync().ConfigureAwait(false) });
            }
            catch (Exception e)
            {
                tcs.SetResult(new Response { Status = 0, Body = "", Error = e.Message });
            }
        }
#endif
    }
}
