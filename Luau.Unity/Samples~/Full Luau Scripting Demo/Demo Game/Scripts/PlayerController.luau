local gravityScale = 1.35
local flapVelocity = 5.25
local minimumRotation = -75
local maximumRotation = 28
local rotationStepDuration = 0.12

local body = nil
local birdTransform = nil
local startPosition = nil
local startRotation = nil
local flightRotations = nil
local rotationElapsed = 0
local rotationStep = 1
local observedRound = nil
local gameState = shared

local function primaryInputPressed()
    if Input.GetKeyDown("Space") or Input.GetMouseButtonDown(0) then
        return true
    end

    return Input.touchCount > 0 and Input.GetTouchPhase(0) == "Began"
end

local function playIfAssigned(audioSource)
    if audioSource ~= nil and audioSource.hasClip then
        audioSource:Play()
    end
end

local function resetBird()
    body.simulated = false
    body.position = startPosition
    body.linearVelocity = vector.create(0, 0, 0)
    body.angularVelocity = 0
    body.gravityScale = gravityScale
    rotationElapsed = 0
    rotationStep = 1
    birdTransform.localRotation = startRotation
end

local function flap()
    body.linearVelocity = vector.create(0, flapVelocity, 0)
    rotationElapsed = 0
    rotationStep = 1
    birdTransform.localRotation = flightRotations[rotationStep]
    playIfAssigned(refs.flapAudio)
end

return {
    start = function()
        body = self:GetComponent("Rigidbody2D")
        if body == nil then
            error("PlayerController requires a Rigidbody2D on self")
        end

        birdTransform = self.transform
        startPosition = body.position
        startRotation = birdTransform.localRotation
        flightRotations = {
            Quaternion.Euler(vector.create(0, 0, maximumRotation)),
            Quaternion.Euler(vector.create(0, 0, 10)),
            Quaternion.Euler(vector.create(0, 0, -12)),
            Quaternion.Euler(vector.create(0, 0, -38)),
            Quaternion.Euler(vector.create(0, 0, minimumRotation)),
        }
        local _initialVelocity = body.linearVelocity
        local _flapHasClip = refs.flapAudio.hasClip
        local _hitHasClip = refs.hitAudio.hasClip
        observedRound = gameState.round
        resetBird()
    end,

    update = function(_deltaTime)
        if gameState.round ~= observedRound then
            observedRound = gameState.round
            resetBird()
        end

        if gameState.phase == "ready" then
            if primaryInputPressed() then
                gameState.phase = "playing"
                body.simulated = true
                flap()
            end
            return
        end

        if gameState.phase == "playing" then
            if primaryInputPressed() then
                flap()
            end
        end
    end,

    lateUpdate = function(deltaTime)
        if gameState.phase ~= "playing" then
            return
        end

        rotationElapsed += deltaTime
        local nextStep = math.clamp(
            math.floor(rotationElapsed / rotationStepDuration) + 1,
            1,
            #flightRotations)
        if nextStep ~= rotationStep then
            rotationStep = nextStep
            birdTransform.localRotation = flightRotations[rotationStep]
        end
    end,

    collisionEnter2D = function(_other, _contactPoint, _contactNormal)
        if gameState.phase == "playing" then
            gameState.phase = "gameOver"
            playIfAssigned(refs.hitAudio)
        end
    end,

    destroy = function()
        if body ~= nil then
            body.simulated = false
        end
    end,
}
