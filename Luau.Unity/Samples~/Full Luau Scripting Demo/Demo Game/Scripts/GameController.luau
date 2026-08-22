local restartDelaySeconds = 0.45
local gameOverElapsed = 0
local previousPhase = nil
local gameState = shared

local function primaryInputPressed()
    if Input.GetKeyDown("Space") or Input.GetMouseButtonDown(0) then
        return true
    end

    return Input.touchCount > 0 and Input.GetTouchPhase(0) == "Began"
end

local function updateText()
    local score = math.floor(gameState.score or 0)
    refs.scoreText.text = tostring(score)

    if gameState.phase == "ready" then
        refs.messageText.text = "SPACE / CLICK / TAP\nTO FLAP"
    elseif gameState.phase == "gameOver" then
        refs.messageText.text = "GAME OVER\nSPACE / CLICK / TAP TO RESTART"
    else
        refs.messageText.text = ""
    end
end

return {
    start = function()
        gameState.phase = "ready"
        gameState.score = 0
        gameState.round = 1
        previousPhase = gameState.phase
        updateText()
    end,

    update = function(deltaTime)
        if gameState.phase ~= previousPhase then
            if gameState.phase == "gameOver" then
                gameOverElapsed = 0
            end
            previousPhase = gameState.phase
        end

        if gameState.phase == "gameOver" then
            gameOverElapsed += deltaTime
            if gameOverElapsed >= restartDelaySeconds and primaryInputPressed() then
                gameState.score = 0
                gameState.round = (gameState.round or 0) + 1
                gameState.phase = "ready"
                previousPhase = gameState.phase
            end
        end

        updateText()
    end,
}
