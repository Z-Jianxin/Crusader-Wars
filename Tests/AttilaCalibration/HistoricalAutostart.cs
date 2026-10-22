internal static class HistoricalAutostart
{
    public static string Script(string logFunction) => """

-- The native frontend assigns m_root after UICreated; let that event finish and
-- give the main menu time to settle before invoking its verified historical API.
local cc_calibration_auto_scheduled = false
local cc_calibration_auto_started = false
local cc_calibration_auto_attempts = 0
local function cc_calibration_auto_try_launch()
    if cc_calibration_auto_started then return end
    cc_calibration_auto_attempts = cc_calibration_auto_attempts + 1
    if not m_root then
        cc_calibration_frontend_log("auto wait: native m_root is not ready")
        if cc_calibration_auto_attempts < 30 then
            tm:callback(cc_calibration_auto_try_launch, 1000, "cc_calibration_auto_ready")
        else
            cc_calibration_frontend_log("auto launch abandoned: native UI root did not become ready")
        end
        return
    end
    cc_calibration_auto_started = true
    cc_calibration_frontend_log("auto launching native Adrianople after UICreated and ready delay")
    local ok, err = pcall(function() FrontEnd.start_historic_battle("Adrianople") end)
    cc_calibration_frontend_log("auto launch return: " .. tostring(ok) .. " " .. tostring(err))
end
AddEventCallBack("UICreated", function(context)
    if cc_calibration_auto_scheduled then return end
    cc_calibration_auto_scheduled = true
    cc_calibration_frontend_log("UICreated: scheduling native historical launch in 5 seconds")
    local ok, err = pcall(function() tm:callback(cc_calibration_auto_try_launch, 5000, "cc_calibration_auto_ready") end)
    cc_calibration_frontend_log("auto timer registration: " .. tostring(ok) .. " " .. tostring(err))
end)

""".Replace("cc_calibration_frontend_log", logFunction);
}
