-- Pipe pairs are spawned, not placed in the scene. spawnPrefab accepts only the
-- names listed in this behaviour's Prefab References, and refuses once the
-- behaviour already owns Max Spawned Objects live instances. Whatever this
-- script spawns is destroyed with the behaviour that spawned it.
local pipePairPrefab = "pipePair"
local pipePairCount = 2

local pipeSpeed = 2.35
local pipeSpacing = 5.5
local leadInX = 4
local recycleX = -6
local minimumGapCenterY = -1.25
local maximumGapCenterY = 1.75

local pipePairs = {}
local scored = {}
local pipeZ = 0
local observedRound = nil
local gameState = shared

local function randomGapCenter()
    return minimumGapCenterY +
        math.random() * (maximumGapCenterY - minimumGapCenterY)
end

local function setPairPosition(pair, x, y)
    pair.position = vector.create(x, y, pipeZ)
end

local function resetPipes()
    for index = 1, #pipePairs do
        setPairPosition(
            pipePairs[index],
            leadInX + (index - 1) * pipeSpacing,
            randomGapCenter())
        scored[index] = false
    end
end

local function playScoreSound()
    if refs.scoreAudio ~= nil and refs.scoreAudio.hasClip then
        refs.scoreAudio:Play()
    end
end

return {
    start = function()
        for index = 1, pipePairCount do
            -- spawnPrefab returns a GameObject capability for the new instance.
            -- Only the transform is needed here, so that is all this script keeps.
            local spawned = spawnPrefab(pipePairPrefab)
            pipePairs[index] = spawned.transform
            scored[index] = false
        end

        -- Instances spawn at this behaviour's own position, so one read gives
        -- the depth the pipes stay on.
        pipeZ = pipePairs[1].position.z

        -- Touch each capability once inside the generous start lane so the first
        -- frame hook is not charged for JIT-ing its dispatch thunk.
        local _scoreHasClip = refs.scoreAudio.hasClip
        observedRound = gameState.round
        resetPipes()
    end,

    fixedUpdate = function(fixedDeltaTime)
        if gameState.round ~= observedRound then
            observedRound = gameState.round
            resetPipes()
        end

        if gameState.phase ~= "playing" then
            return
        end

        local birdX = refs.bird.position.x
        for index = 1, #pipePairs do
            local pair = pipePairs[index]
            local position = pair.position
            local nextX = position.x - pipeSpeed * fixedDeltaTime

            if nextX < recycleX then
                local otherIndex = index == 1 and 2 or 1
                nextX = pipePairs[otherIndex].position.x + pipeSpacing
                setPairPosition(pair, nextX, randomGapCenter())
                scored[index] = false
            else
                setPairPosition(pair, nextX, position.y)
            end

            if not scored[index] and nextX < birdX then
                scored[index] = true
                gameState.score = (gameState.score or 0) + 1
                playScoreSound()
            end
        end
    end,
}
