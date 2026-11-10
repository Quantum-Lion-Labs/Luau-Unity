-- Consumer probe: spawns the prefab named "child" once.
return {
    start = function()
        spawnPrefab("child")
    end,
}
