-- Consumer probe: records what a physics callback can read from the other
-- object, then tries to change it. The probe compares the Unity objects
-- afterwards with what this behaviour was granted.
local state = shared

local function record(line)
    state.physicsReport = (state.physicsReport or "") .. line .. "\n"
end

return {
    start = function()
        state.physicsReport = ""
        -- Spawned at this behaviour's position, so the block's collider
        -- overlaps this trigger on the next physics step.
        local spawned = spawnPrefab("block")
        record("spawned|" .. spawned.name)
    end,

    triggerEnter2D = function(other)
        local name = other.name
        local transform = other.transform
        local position = transform.position
        local rotation = transform.rotation
        record(
            "seen|" .. name ..
            "|" .. other.tag ..
            "|" .. tostring(other.layer) ..
            "|" .. tostring(other.activeSelf) ..
            "|" .. tostring(other.activeInHierarchy) ..
            "|" .. tostring(other:CompareTag("Untagged")) ..
            "|" .. tostring(position.x) ..
            "|" .. tostring(rotation.w))

        other.name = name .. " [written]"
        transform.position = vector.create(50, 50, 0)
        other:GetComponent("Collider2D").isTrigger = true
        other:SetActive(false)
        record("wrote|" .. name)
    end,
}
