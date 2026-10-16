using System;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.JSInterop;

namespace RoomScheduler.Web.Services
{
    /// <summary>
    /// The planner session for this browser. The public data files (rooms,
    /// buildings, history) are downloaded with the app. Files the user adds,
    /// faculty profiles and offline rooms are saved in the browser's local
    /// storage, so they stay on this computer. Nothing is uploaded anywhere.
    /// </summary>
    public class PlanningService : PlanningSession
    {
        private readonly IJSInProcessRuntime _js;

        public PlanningService(IJSRuntime js)
        {
            _js = (IJSInProcessRuntime)js;
        }

        public async Task InitAsync(HttpClient http)
        {
            async Task<string> Fetch(string name)
            {
                try { return await http.GetStringAsync("data/" + name); }
                catch (HttpRequestException) { return ""; }
            }

            var data = new DataFiles(
                await Fetch("gpc_rooms_features.csv"),
                await Fetch("gdc_rooms.csv"),
                await Fetch("lecture_capture_rooms.csv"),
                await Fetch("buildings.csv"),
                await Fetch("course_history.csv"));

            Initialize(new BrowserStore(_js), data);
        }

        /// <summary>The browser's local storage, under one prefix.</summary>
        private class BrowserStore : IKeyValueStore
        {
            private const string Prefix = "roomscheduler:";
            private readonly IJSInProcessRuntime _js;

            public BrowserStore(IJSInProcessRuntime js) => _js = js;

            public string? Get(string key) => _js.Invoke<string?>("localStorage.getItem", Prefix + key);

            public void Set(string key, string value)
            {
                try
                {
                    _js.InvokeVoid("localStorage.setItem", Prefix + key, value);
                }
                catch (JSException e)
                {
                    throw new InvalidOperationException(
                        "The browser's storage is full. Remove some files and try again.", e);
                }
            }

            public void Remove(string key) => _js.InvokeVoid("localStorage.removeItem", Prefix + key);
        }
    }
}
