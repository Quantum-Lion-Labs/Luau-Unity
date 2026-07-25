local pipeSpeed = 2.35
local pipeSpacing = 5.5
local recycleX = -6
local minimumGapCenterY = -1.25
local maximumGapCenterY = 1.75

local pipePairs = {
    refs.pipePairOne,
    refs.pipePairTwo,
}
local initialPositions = {}
local scored = { false, false }
local observedRound = nil
local gameState = shared

local function randomGapCenter()
    return minimumGapCenterY +
        math.random() * (maximumGapCenterY - minimumGapCenterY)
end

local function setPairPosition(pair, x, y, z)
    pair.position = vector.create(x, y, z)
end

local function resetPipes()
    for index = 1, #pipePairs do
        local initial = initialPositions[index]
        setPairPosition(
            pipePairs[index],
            initial.x,
            randomGapCenter(),
            initial.z)
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
        for index = 1, #pipePairs do
            initialPositions[index] = pipePairs[index].position
        end

        local _scoreHasClip = refs.scoreAudio.hasClip
        observedRound = gameState.round
        resetPipes()
    end,

    fixedUpdate = function(deltaTime)
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
            local nextX = position.x - pipeSpeed * deltaTime

            if nextX < recycleX then
                local otherIndex = index == 1 and 2 or 1
                nextX = pipePairs[otherIndex].position.x + pipeSpacing
                setPairPosition(pair, nextX, randomGapCenter(), position.z)
                scored[index] = false
            else
                setPairPosition(pair, nextX, position.y, position.z)
            end

            if not scored[index] and nextX < birdX then
                scored[index] = true
                gameState.score = (gameState.score or 0) + 1
                playScoreSound()
            end
        end
    end,
}
