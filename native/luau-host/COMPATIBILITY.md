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

A thread's current frame holds at most `LUAU_HOST_FRAME_SLOT_LIMIT` (8000,
Luau's `LUAI_MAXCSTACK`) values through the ABI. Protected operations reserve
the slots for their result and error object before they run. When that is
impossible, they return `LUAU_HOST_STATUS_STACK_LIMIT` without touching the
stack and without an error object; `LUAU_HOST_FEATURE_STACK_LIMIT` advertises
this contract. Shrinking `set_top`, `insert`, `remove`, `replace`, `check`, and
reference release need no free slot and cannot fail, so a caller can always
empty a frame, including one Luau filled past the limit. A managed function
that returns `LUAU_HOST_CALLBACK_ERROR` may drop its arguments first; the host
raises the topmost value of the non-empty frame, so a callback whose arguments
filled its frame can still report a failure.

Host suspension uses Luau's break continuations. `luau_host_suspend` preserves
arguments on the calling coroutine and stops its waiting `coroutine.resume` or
`coroutine.wrap` ancestors. `luau_host_resume_target` identifies the borrowed
thread on which callback results or errors must be pushed. Resuming the outer
state completes that thread before its ancestors; resetting it resets the
suspended chain. Ordinary `luau_host_yield` and script yields retain their
existing script-visible behavior. The host reserves native thread userdata
and the debug-interrupt hook for these continuation links.

Each plugin's dynamic dependencies are part of this contract. The Android
plugins require API level 26 and load only `libc.so`, `libm.so`, and `libdl.so`;
the C++ runtime is linked statically, which is valid because no C++ type,
allocation, or exception crosses the C ABI. They therefore work with both the
Activity and the GameActivity application entry points, whether or not the
player contains `libc++_shared.so`. The Linux plugin loads only `libc.so.6`,
`libm.so.6`, and the dynamic loader, and requires nothing newer than glibc 2.35.
`native-artifacts` rejects a shipping build, and `package-static` a committed
plugin, that needs any other library.

For a native source change, manually dispatch the **Build Luau Host** workflow
with `refresh-artifacts=true` to generate audited replacement Windows, Android,
and Linux plugins. Download their shipping outputs and commit them with the
matching managed DLL.
Normal pull-request and main-branch CI always checks that committed plugins
match a fresh build; the manual refresh option only generates replacements.
