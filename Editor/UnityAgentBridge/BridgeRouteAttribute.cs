using System;

namespace UnityAgentBridge
{
    /// <summary>
    /// Marks a static method as a bridge route handler.
    /// Discovered at startup via TypeCache — no manual registration needed.
    /// A method may carry multiple attributes to handle several routes (e.g., GET + PUT on same path).
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public sealed class BridgeRouteAttribute : Attribute
    {
        /// <summary>HTTP method: "GET", "POST", "PUT", "DELETE"</summary>
        public string Method { get; }

        /// <summary>Route path, e.g. "/health", "/gameobject/{id}", "/component/{id}/{type}"</summary>
        public string Path { get; }

        /// <summary>Catalog category for /routes grouping (e.g. "meta", "gameobjects")</summary>
        public string Category { get; set; } = "uncategorized";

        /// <summary>Human-readable description shown in /routes catalog</summary>
        public string Description { get; set; } = "";

        /// <summary>If true, dispatch to RunOnMainThreadRead instead of RunOnMainThread</summary>
        public bool ReadOnly { get; set; }

        /// <summary>If true, call handler directly on listener thread (no queue)</summary>
        public bool Direct { get; set; }

        /// <summary>Default timeout in ms (0 = server default 5000ms)</summary>
        public int TimeoutDefault { get; set; }

        /// <summary>Minimum allowed timeout in ms (0 = 250ms)</summary>
        public int TimeoutMin { get; set; }

        /// <summary>Maximum allowed timeout in ms (0 = 180000ms)</summary>
        public int TimeoutMax { get; set; }

        /// <summary>HTTP status code on handler exception (default 500)</summary>
        public int FailCode { get; set; } = 500;

        public BridgeRouteAttribute(string method, string path)
        {
            Method = method;
            Path = path;
        }
    }
}
