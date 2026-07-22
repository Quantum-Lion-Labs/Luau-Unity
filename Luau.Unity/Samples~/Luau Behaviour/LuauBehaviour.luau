local elapsed = 0

return {
    start = function()
        self.name = self.name .. " (Luau)"
    end,

    update = function(deltaTime)
        elapsed += deltaTime
        self.transform:Translate(vector.create(deltaTime, 0, 0))
    end,

    destroy = function()
        print("Luau Behaviour ran for", elapsed, "seconds")
    end,
}
