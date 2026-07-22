local elapsed = 0
local spawnedPrefab = nil

return {
    start = function()
        self.name = self.name .. " (Luau)"
        spawnedPrefab = spawnPrefab("movingPrefab")
        spawnedPrefab.name = "Spawned and moved by Luau"
    end,

    update = function(deltaTime)
        elapsed += deltaTime
        refs.movingSceneObject.transform:Translate(vector.create(0, deltaTime, 0))
        spawnedPrefab.transform:Translate(vector.create(deltaTime, 0, 0))
    end,

    destroy = function()
        print("Luau Behaviour ran for", elapsed, "seconds")
    end,
}
