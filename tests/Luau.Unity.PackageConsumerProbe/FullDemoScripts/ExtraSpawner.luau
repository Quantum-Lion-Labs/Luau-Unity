-- Consumer probe: spawns one "block" when the host asks through shared. A
-- "protected" attempt records whether the spawn was admitted; a "strict"
-- attempt lets a refusal fail the hook so the host sees its error.
local state = shared

return {
    update = function(_deltaTime)
        local attempt = state.extraAttempt
        if attempt == "protected" then
            state.extraAttempt = nil
            state.extraResult = if pcall(spawnPrefab, "block") then "spawned" else "refused"
        elseif attempt == "strict" then
            state.extraAttempt = nil
            spawnPrefab("block")
            state.extraResult = "spawned"
        end
    end,
}
