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

Reference tokens are positive `int32_t` values, unique among the tokens live
in the process and valid only on the root that created them. They are issued
in ascending order; after `INT32_MAX` the order continues from 1, passing every
value that is live. Until the range has been used once no value is issued
twice; after that a released value may be issued again by any later creation,
with no minimum delay. Creation reports
`LUAU_HOST_STATUS_RESOURCE_EXHAUSTED` only while every positive `int32_t` value
is live. Through 0.3.1, values were never reused, and creation failed for every
root for the rest of the process once 2,147,483,647 references had been
created. Callback registration IDs are
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

Luau runs code only on a thread whose status is OK. The operations that can run
Luau code through a call, a metamethod, or an import resolved while loading
(`table_get`, `table_set`, `global_get`, `global_set`, `to_display_string`,
`load`, `open_library`, and `pcall`) return
`LUAU_HOST_STATUS_THREAD_NOT_RUNNABLE` for a thread that yielded, is suspended
by a host break, or ended with an error, after argument validation and before
the slot reservation. The refusal runs nothing, consumes nothing, and appends
no error object; `LUAU_HOST_FEATURE_RUNNABLE_THREAD_GUARD` advertises it. The
interpreter would otherwise leave a function started there at its first
safepoint with the thread's frame and stack broken, and the next resume would
crash. A caller does the same work on a runnable thread of the same root:
`stack_move` carries the inputs there, `global_push` makes a thread's globals
reachable from another thread, and a thread created from the refused one
shares its globals for `load` and `open_library`. `pcall` used to report this
case as `LUAU_HOST_STATUS_INVALID_ARGUMENT`.

Interrupt delivery can be throttled per root. After
`luau_host_interrupt_configure`, the host counts EXECUTION safepoints itself,
enforces the configured count limit exactly (the poll runs at safepoint
`limit + 1`), reads its monotonic clock every 1 to 128 counted safepoints
against the configured deadline, and otherwise calls the poll only when
`luau_host_interrupt_request` has raised attention. A request is the one host
call any thread may make concurrently with execution on its root; it is a
lock-free store and must return before `luau_host_state_close` begins. A
throttled root never forwards GC notifications. A fresh install still polls at
every safepoint, so `LUAU_HOST_FEATURE_INTERRUPT_THROTTLE` advertises an
additive contract.

Host suspension uses Luau's break continuations. `luau_host_suspend` preserves
arguments on the calling coroutine and stops its waiting `coroutine.resume` or
`coroutine.wrap` ancestors. `luau_host_resume_target` identifies the borrowed
thread on which callback results or errors must be pushed. Resuming the outer
state completes that thread before its ancestors; resetting it resets the
suspended chain. Ordinary `luau_host_yield` and script yields retain their
existing script-visible behavior. The host reserves native thread userdata
and the debug-interrupt hook for these continuation links.

Each plugin's dynamic dependencies are part of this contract. The Windows
plugin imports only `KERNEL32.dll`, with no delay-load import or forwarded
export: the Microsoft C and C++ runtimes are linked statically (`/MT`), which is
valid for the same reason, and because the one allocation a caller receives,
a compile output buffer, is released by `luau_host_buffer_free` in the runtime
that allocated it. It therefore needs no Visual C++ redistributable. The Android
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
