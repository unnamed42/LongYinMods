using System;
using System.Collections.Generic;
using MelonLoader;
using MelonMCP.Server;
using Newtonsoft.Json.Linq;

namespace MelonMCP.Tools
{
    /// <summary>
    /// Get general game information
    /// </summary>
    public class GetGameInfoToolDefinition : ToolDefinitionBase
    {
        public override string Name => "get_game_info";
        public override string Description => @"Get information about the running game including:
- Game name, developer, version
- Unity version
- Platform information
- MelonLoader version
- Current scene information
- Screen resolution";

        protected override ToolInputSchema GetInputSchema()
        {
            return new ToolInputSchema
            {
                Properties = new Dictionary<string, ToolPropertySchema>()
            };
        }

        public override CallToolResult Execute(Dictionary<string, JToken> arguments)
        {
            try
            {
                var json = UnityHelper.GetGameInfoJson();
                return TextResult(json);
            }
            catch (Exception ex)
            {
                return ErrorResult($"Failed to get game info: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Get time information
    /// </summary>
    public class GetTimeInfoToolDefinition : ToolDefinitionBase
    {
        public override string Name => "get_time_info";
        public override string Description => @"Get Unity time information including:
- Current time, delta time, fixed delta time
- Time scale (for slow-motion or pause detection)
- Frame count
- Real time since startup";

        protected override ToolInputSchema GetInputSchema()
        {
            return new ToolInputSchema
            {
                Properties = new Dictionary<string, ToolPropertySchema>()
            };
        }

        public override CallToolResult Execute(Dictionary<string, JToken> arguments)
        {
            try
            {
                var timeInfo = UnityHelper.GetTimeInfo();
                return JsonResult(timeInfo);
            }
            catch (Exception ex)
            {
                return ErrorResult($"Failed to get time info: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Take a screenshot
    /// </summary>
    public class TakeScreenshotToolDefinition : ToolDefinitionBase
    {
        public override string Name => "take_screenshot";
        public override string Description => "Capture a screenshot of the current game view. Returns the image as base64-encoded PNG data.";

        protected override ToolInputSchema GetInputSchema()
        {
            return new ToolInputSchema
            {
                Properties = new Dictionary<string, ToolPropertySchema>
                {
                    ["saveToFile"] = new ToolPropertySchema
                    {
                        Type = "string",
                        Description = "Optional file path to save the screenshot (in addition to returning it)"
                    }
                }
            };
        }

        public override CallToolResult Execute(Dictionary<string, JToken> arguments)
        {
            var saveToFile = GetStringArg(arguments, "saveToFile");

            try
            {
                var screenshotData = UnityHelper.CaptureScreenshot();

                if (screenshotData == null || screenshotData.Length == 0)
                {
                    return ErrorResult("Failed to capture screenshot - screenshot API may not be available");
                }

                // Save to file if requested
                if (!string.IsNullOrEmpty(saveToFile))
                {
                    try
                    {
                        System.IO.File.WriteAllBytes(saveToFile, screenshotData);
                        MelonMCPPlugin.Logger?.Msg($"Screenshot saved to: {saveToFile}");
                    }
                    catch (Exception ex)
                    {
                        MelonMCPPlugin.Logger?.Warning($"Failed to save screenshot: {ex.Message}");
                    }
                }

                return ImageResult(screenshotData, "image/png");
            }
            catch (Exception ex)
            {
                return ErrorResult($"Failed to take screenshot: {ex.Message}");
            }
        }
    }
}
