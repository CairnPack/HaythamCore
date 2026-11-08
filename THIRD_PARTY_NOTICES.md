# 第三方声明

Haytham Core 以 GPL-3.0-or-later 授权（见 [LICENSE](LICENSE)）。本文件列出它用到的第三方组件及其授权。新加 NuGet 包之前先确认授权与 GPL-3.0 兼容，并在这里登记；Haytham Core 不加入 GPL 授权的依赖。

## 随 `CairnPack.Haytham.Core.Store` 分发

这三个包由 `Haytham.Core.Store` 引用，用来把 Store 接成 Haytham.Runtime 的模块。

### Haytham.Runtime

- 版本：0.1.0
- 用在：`src/Haytham.Core.Store`，提供模块生命周期、Task 与模块清单生成器。
- 来源：<https://github.com/CairnPack/HaythamRuntime>
- 授权：MIT，Copyright (c) 2026 CairnPack，全文见来源仓库的 LICENSE。

### Microsoft.Extensions.Logging.Abstractions、System.Threading.RateLimiting

- 版本：10.0.14（由 Haytham.Runtime 间接引入）
- 用在：Haytham.Runtime 的日志接口与按键排队。
- 来源：<https://github.com/dotnet/runtime>
- 授权：MIT，Copyright (c) .NET Foundation，授权全文与下文 Microsoft.Extensions.TimeProvider.Testing 一节相同。

## 只在测试中使用（不随 NuGet 包或任何发行物分发）

### Microsoft.Extensions.TimeProvider.Testing

- 版本：10.10.0
- 用在：`tests/Haytham.Core.Store.Tests`，提供 `FakeTimeProvider`，让等待与超时的测试推进假时钟，不必真的等待。
- 来源：<https://github.com/dotnet/extensions>
- 授权：MIT

```
The MIT License (MIT)

Copyright (c) .NET Foundation. All rights reserved.

All rights reserved.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
