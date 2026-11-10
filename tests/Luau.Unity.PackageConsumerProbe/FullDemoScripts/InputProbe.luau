-- Consumer probe: reads the key names the host put in shared through the
-- runtime's Input library. Each result is recorded before the next read, so
-- the host can tell which read failed.
local state = shared

local function describe(pressed)
    return if pressed then "pressed" else "released"
end

return {
    start = function()
        state.allowedRead = describe(Input.GetKey(state.allowedKey)) ..
            "/" .. describe(Input.GetKeyDown(state.allowedKey))

        if state.checkPointer then
            state.pointerRead = describe(Input.GetMouseButton(0)) ..
                "/" .. describe(Input.GetMouseButtonDown(0)) ..
                "/" .. tostring(Input.touchCount)
        end

        state.unlistedRead = describe(Input.GetKey(state.unlistedKey))
    end,
}
