using System.Runtime.CompilerServices;

// The determinism tests reach into the bridge's internal helpers (snapshot layers, script layout)
// to cover paths that a black-box test cannot reach - notably the reflection-interpreted layout
// path used under NativeAOT, which never runs in a JIT-hosted test process.
[assembly: InternalsVisibleTo("Klotho.Tests")]
