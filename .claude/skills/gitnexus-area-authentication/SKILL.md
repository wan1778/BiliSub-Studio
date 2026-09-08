---
name: gitnexus-area-authentication
description: "Skill for the Authentication area of BiliSub-Studio. 48 symbols across 6 files."
---

# Authentication

48 symbols | 6 files | Cohesion: 72%

## When to Use

- Working with code in `csharp/`
- Understanding how Encode, PollQrAsync, StartQrAsync work
- Modifying authentication-related functionality

## Key Files

| File | Symbols |
|------|---------|
| `csharp/src/BiliSubStudio.Core/Authentication/QrMatrixEncoder.cs` | AppendBits, DrawCodewords, DrawVersionBits, Encode, GfMultiply (+12) |
| `csharp/src/BiliSubStudio.Core/Authentication/SessionStore.cs` | IsCookieName, IsCookieValue, NormalizeCookie, Protect, SetCookieAsync (+12) |
| `csharp/src/BiliSubStudio.Core/Authentication/BilibiliAuthService.cs` | CookieFromQr, CreateRequest, EnsureApiSuccess, GetJsonAsync, PollQrAsync (+5) |
| `csharp/tests/BiliSubStudio.Core.ContractTests/Program.cs` | SessionCookieContractAsync, SessionCookieInjectionContractAsync |
| `csharp/src/BiliSubStudio.App/Pages/SubtitlePage.xaml.cs` | Load_Click |
| `csharp/src/BiliSubStudio.Core/Application/BiliSubApplication.cs` | GetMetadataAsync |

## Entry Points

Start here when exploring this area:

- **`Encode`** (Method) — `csharp/src/BiliSubStudio.Core/Authentication/QrMatrixEncoder.cs:16`
- **`PollQrAsync`** (Method) — `csharp/src/BiliSubStudio.Core/Authentication/BilibiliAuthService.cs:54`
- **`StartQrAsync`** (Method) — `csharp/src/BiliSubStudio.Core/Authentication/BilibiliAuthService.cs:42`
- **`StatusAsync`** (Method) — `csharp/src/BiliSubStudio.Core/Authentication/BilibiliAuthService.cs:20`
- **`SetCookieAsync`** (Method) — `csharp/src/BiliSubStudio.Core/Authentication/BilibiliAuthService.cs:32`

## Key Symbols

| Symbol | Type | File | Line |
|--------|------|------|------|
| `Encode` | Method | `csharp/src/BiliSubStudio.Core/Authentication/QrMatrixEncoder.cs` | 16 |
| `PollQrAsync` | Method | `csharp/src/BiliSubStudio.Core/Authentication/BilibiliAuthService.cs` | 54 |
| `StartQrAsync` | Method | `csharp/src/BiliSubStudio.Core/Authentication/BilibiliAuthService.cs` | 42 |
| `StatusAsync` | Method | `csharp/src/BiliSubStudio.Core/Authentication/BilibiliAuthService.cs` | 20 |
| `SetCookieAsync` | Method | `csharp/src/BiliSubStudio.Core/Authentication/BilibiliAuthService.cs` | 32 |
| `NormalizeCookie` | Method | `csharp/src/BiliSubStudio.Core/Authentication/SessionStore.cs` | 121 |
| `SetCookieAsync` | Method | `csharp/src/BiliSubStudio.Core/Authentication/SessionStore.cs` | 41 |
| `LoadAsync` | Method | `csharp/src/BiliSubStudio.Core/Authentication/SessionStore.cs` | 18 |
| `GetMetadataAsync` | Method | `csharp/src/BiliSubStudio.Core/Application/BiliSubApplication.cs` | 101 |
| `DeleteAsync` | Method | `csharp/src/BiliSubStudio.Core/Authentication/BilibiliAuthService.cs` | 40 |
| `DeleteAsync` | Method | `csharp/src/BiliSubStudio.Core/Authentication/SessionStore.cs` | 68 |
| `DeleteTemporaryAsync` | Method | `csharp/src/BiliSubStudio.Core/Authentication/SessionStore.cs` | 110 |
| `WriteNetscapeFileAsync` | Method | `csharp/src/BiliSubStudio.Core/Authentication/SessionStore.cs` | 82 |
| `Get` | Function | `csharp/src/BiliSubStudio.Core/Authentication/QrMatrixEncoder.cs` | 181 |
| `AppendBits` | Method | `csharp/src/BiliSubStudio.Core/Authentication/QrMatrixEncoder.cs` | 59 |
| `DrawCodewords` | Method | `csharp/src/BiliSubStudio.Core/Authentication/QrMatrixEncoder.cs` | 217 |
| `DrawVersionBits` | Method | `csharp/src/BiliSubStudio.Core/Authentication/QrMatrixEncoder.cs` | 190 |
| `GfMultiply` | Method | `csharp/src/BiliSubStudio.Core/Authentication/QrMatrixEncoder.cs` | 82 |
| `Interleave` | Method | `csharp/src/BiliSubStudio.Core/Authentication/QrMatrixEncoder.cs` | 64 |
| `LinePenalty` | Method | `csharp/src/BiliSubStudio.Core/Authentication/QrMatrixEncoder.cs` | 254 |

## Execution Flows

| Flow | Type | Steps |
|------|------|-------|
| `SaveCookie_Click → DATA_BLOB` | cross_community | 7 |
| `Qr_Click → SetFunction` | cross_community | 6 |
| `Qr_Click → GfMultiply` | cross_community | 6 |
| `SaveCookie_Click → CryptProtectData` | cross_community | 6 |
| `SaveCookie_Click → CryptUnprotectData` | cross_community | 6 |
| `SaveCookie_Click → LocalFree` | cross_community | 6 |
| `PollQrAsync → DATA_BLOB` | cross_community | 6 |
| `OnLoaded → IsCookieValue` | cross_community | 5 |
| `Qr_Click → AppendBits` | cross_community | 5 |
| `SaveCookie_Click → IsCookieName` | cross_community | 5 |

## How to Explore

1. `context({name: "Encode"})` — see callers and callees
2. `query({search_query: "authentication"})` — find related execution flows
3. Read key files listed above for implementation details
4. `explain({target: "<file or symbol>"})` — persisted taint findings (source→sink data flows), when indexed with `--pdg`
