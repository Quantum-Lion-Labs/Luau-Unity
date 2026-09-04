# Native compatibility contract

`Luau.Unity` and `luau_host` are one compatibility unit. The Stage 6 host ABI
is exactly **2.0**: both major and minor must match the managed declarations,
record layouts, type tags, required feature mask, export allowlist, upstream
revision hash, and host build fingerprint. Prefix/minor-forward compatibility
is not claimed because no independently versioned native consumer exists.

ABI compatibility is separate from compiler/bytecode identity. ABI 2.0 defines
the C calling contract; persistent bytecode is accepted only when its compiler
identity matches the exact pinned upstream revision and host input fingerprint.
Changing the compiler options, upstream Luau commit, host sources, reference
token implementation, header, or export allowlist changes that fingerprint
even when the ABI record layout remains 2.0.

Reference tokens are positive process-unique `int32_t` values allocated
monotonically and never reused. Exhaustion is permanent for the process and is
reported as `LUAU_HOST_STATUS_RESOURCE_EXHAUSTED`. Callback registration IDs are
copied into native closure metadata and observed only during the corresponding
active callback. GC interrupt notifications are observation-only. Destructor
callbacks may only release or enqueue their opaque token and must not call the
host, block, allocate substantially, access Unity APIs, or unwind.

Host suspension uses Luau's break continuations. `luau_host_suspend` preserves
arguments on the calling coroutine and stops its waiting `coroutine.resume` or
`coroutine.wrap` ancestors. `luau_host_resume_target` identifies the borrowed
thread on which callback results or errors must be pushed. Resuming the outer
state completes that thread before its ancestors; resetting it resets the
suspended chain. Ordinary `luau_host_yield` and script yields retain their
existing script-visible behavior. The host reserves native thread userdata
and the debug-interrupt hook for these continuation links.

For a native source change, manually dispatch the Windows and Android build
workflows with `refresh-artifacts=true` to generate audited replacement plugins.
Download their shipping outputs and commit them with the matching managed DLL.
Normal pull-request and main-branch CI always checks that committed plugins
match a fresh build; the manual refresh option only generates replacements.
