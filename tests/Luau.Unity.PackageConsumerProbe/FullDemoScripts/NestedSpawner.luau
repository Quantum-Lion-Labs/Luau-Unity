-- Consumer probe: every copy of this behaviour spawns two more copies of its
-- own GameObject, listed as the prefab named "child", until it is three spawns
-- deep. The depth is the number of "(Clone)" suffixes Unity gave the name.
-- A refused spawn is counted rather than failing start, so the number of live
-- copies shows exactly how many spawns were admitted.
local state = shared
local maximumDepth = 3

local function depthOf(name)
    local _, count = string.gsub(name, "%(Clone%)", "")
    return count
end

return {
    start = function()
        if depthOf(self.name) >= maximumDepth then
            return
        end

        for _ = 1, 2 do
            if not pcall(spawnPrefab, "child") then
                state.refused = (state.refused or 0) + 1
            end
        end
    end,
}
