# Third-party notices

Code Wicket redistributes the third-party components below, in the `.vsix` and
in the bundled engine. Their licences are reproduced here as required.

**Markdig ships as `Markdig.Signed.dll`, not `Markdig.dll`.** We consume the authors' own
strong-named build of the same library, because an *unsigned* assembly name can be captured
process-wide by any other Visual Studio extension's pkgdef registration — see the rationale in
`src/CodeWicket.UI/CodeWicket.UI.csproj`. Same code, same copyright, same licence; only the
package and assembly name differ, so the notice below is the one that applies.

---

## Markdig — BSD 2-Clause

Copyright (c) 2018-2019, Alexandre Mutel
All rights reserved.

Redistribution and use in source and binary forms, with or without modification, are permitted
provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this list of conditions
   and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright notice, this list of
   conditions and the following disclaimer in the documentation and/or other materials provided
   with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR
IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND
FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR
CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER
IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT
OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

---

## Emoji.Wpf and Stfu — WTFPL

Copyright © 2017–2021 Sam Hocevar

Licensed under the Do What The Fuck You Want To Public License, Version 2, December 2004
(Copyright © 2004 Sam Hocevar). Everyone is permitted to copy and distribute verbatim or modified
copies of the licence document, and changing it is allowed as long as the name is changed.

    0. You just DO WHAT THE FUCK YOU WANT TO.

---

## Typography.OpenFont and Typography.GlyphLayout — MIT

From the [LayoutFarm/Typography](https://github.com/LayoutFarm/Typography) project, redistributed
inside the `Emoji.Wpf` NuGet package rather than referenced as packages of their own. The project
declares MIT overall, while noting that individual source files carry the licence of the code they
were ported from — Apache 2.0, MIT and BSD 3-Clause, covering FreeType, SharpFont, Anti-Grain
Geometry, agg-sharp and msdfgen. Consult the file headers in that repository for per-file terms.

---

## MIT-licensed components

The following are distributed under the MIT licence, whose text is reproduced once below.

| Component | Copyright |
|---|---|
| ColorCode.Core | (c) .NET Foundation and Contributors. All rights reserved. |
| MessagePack, MessagePack.Annotations | © Yoshifumi Kawai and contributors. All rights reserved. |
| Nerdbank.Streams | © Andrew Arnott. All rights reserved. |
| Newtonsoft.Json | Copyright © James Newton-King 2008 |
| Microsoft.Bcl.AsyncInterfaces, Microsoft.NET.StringTools, Microsoft.VisualStudio.Threading, Microsoft.VisualStudio.Validation, Microsoft.Win32.Registry, StreamJsonRpc, System.Buffers, System.Collections.Immutable, System.Diagnostics.DiagnosticSource, System.IO.Pipelines, System.Memory, System.Numerics.Vectors, System.Runtime.CompilerServices.Unsafe, System.Security.AccessControl, System.Security.Principal.Windows, System.Text.Encodings.Web, System.Text.Json, System.Threading.Tasks.Dataflow, System.Threading.Tasks.Extensions, System.ValueTuple | © Microsoft Corporation. All rights reserved. |

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
associated documentation files (the "Software"), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute,
sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or
substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT
NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM,
DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT
OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

---

## Not redistributed

The agent backends Code Wicket drives — the Kiro CLI and
`@agentclientprotocol/claude-agent-acp` — are installed and updated by the user, not shipped with
this extension, and carry their own terms.
