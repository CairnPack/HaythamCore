# Agent Note: 子模块 CI 在三个平台运行测试

Status: implemented

## Problem

Haytham Launcher 的 CI 明确不跑子模块的测试，理由是“子模块在自己的 repo 跑 CI”。但本 repo 的 `build` 工作流原来只在 Ubuntu、Windows、macOS 上执行 `dotnet build`，没有运行任何测试。结果是现有测试没有任何地方在三个平台上自动运行；测试项目是 `Haytham.Core.Store.Tests` 与 `Haytham.Core.Testing.Tests`，只在开发者的 Windows 机器上运行过，POSIX 上的行为（例如文件锁的 `flock` 错误码）没有实测。

## Decision

- `.github/workflows/build.yml` 的 `build` 作业按平台分成三个矩阵作业，每个作业在 `dotnet build HaythamCore.slnx -c Release` 之后运行 `dotnet test --solution HaythamCore.slnx -c Release --no-build`。三个平台的测试结果因此分别显示在各自的作业里。
- 用 `--solution` 而不是位置参数，是因为 `global.json` 把测试运行器设成了 Microsoft.Testing.Platform；在这个模式下，`dotnet test` 用 `--solution`、`--project` 指定目标。
- 只运行已有的测试。不在这个改动里加新测试，也不为了在 Linux 或 macOS 上通过而放宽断言或跳过测试；跨平台的差异暴露出来时，按失败本身去修实现或测试对应的模型。

## Alternatives considered

- **只在主 repo 的 CI 里跑子模块测试** — 最有力的理由：一处配置，本机常用的运行环境一致。不采用：子模块是独立发布、独立演进的公开 repo（`Haytham.Core.Store` 还单独发 NuGet 包），它们的测试要能在自己的 repo 里验证；而且主 repo 运行测试的作业只在 Windows 上。
- **新增一个单独的 `test` 作业，与 `build` 并列** — 最有力的理由：构建和测试的失败分开显示。不采用：矩阵里每个平台已经是一个作业，在同一个作业里先构建再测试，可以用 `--no-build` 复用构建结果，不用在每个平台多做一次还原和编译。

## Consequences

- **好处**：现有测试在每个 PR 与 main 的推送上，于三个平台各运行一次。
- **没有验证的部分**：这个工作流在本地无法运行，`dotnet test --solution … --no-build` 的用法只核对过 `dotnet test --help` 的选项，没有在 GitHub Actions 上实际跑过。第一次运行时，Linux 与 macOS 可能暴露此前只在 Windows 上跑过的测试所没发现的跨平台问题。
- **副作用**：作业时间变长；一个挂起的测试会占用作业直到 GitHub 的默认超时（6 小时）。假时钟测试的挂起问题由[假时钟的看门狗](../testing/2026-10-27-fake-clock-watchdog.md)缩短到 30 秒。
