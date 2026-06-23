using System;
using Autodesk.Navisworks.Api.Plugins;

namespace Waabe.Navisworks.Bridge
{
    [Plugin("WaabeNaviBridge",
            "WAABE",
            DisplayName = "WAABE Navisworks Bridge",
            ToolTip = "Starts HTTP bridge for MCP")]
    [AddInPlugin(AddInLocation.None)]
    public class NaviBridgePlugin : AddInPlugin
    {
        private static bool _initialized = false;

        public NaviBridgePlugin()
        {
            InitializeBridge();
        }

        private void InitializeBridge()
        {
            if (_initialized) return;

            try
            {
                BridgeServer.Start();
                _initialized = true;

                // ✅ FIX: Application.WriteLine() n'existe pas — utiliser Console
                Console.WriteLine("✅ WAABE Bridge Server démarré sur http://localhost:5050");
            }
            catch (Exception ex)
            {
                Console.WriteLine("❌ Erreur Bridge : " + ex.Message);
            }
        }

        public override int Execute(params string[] parameters)
        {
            return 0;
        }
    }
}