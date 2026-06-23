using System;
using Autodesk.Navisworks.Api;

namespace Waabe.Navisworks.Bridge.Commands
{
    public class GetModelInfoCommand
    {
        public static object Execute()
        {
            Document doc = Autodesk.Navisworks.Api.Application.ActiveDocument;

            if (doc == null || doc.IsClear)
            {
                return new
                {
                    success = false,
                    command = "get_model_info",
                    error = "No active Navisworks document."
                };
            }

            return new
            {
                success = true,
                command = "get_model_info",
                fileName = doc.FileName ?? "",
                title = doc.Title ?? "",
                modelCount = GetModelCount(doc),
                totalItems = GetTotalItems(doc),
                isModified = doc.IsModified,
                units = doc.Units.ToString()
            };
        }

        private static int GetModelCount(Document doc)
        {
            try
            {
                return doc.Models.Count;
            }
            catch
            {
                return 0;
            }
        }

        private static int GetTotalItems(Document doc)
        {
            int count = 0;

            try
            {
                ModelItemCollection rootItems = doc.Models.CreateCollectionFromRootItems();

                foreach (ModelItem item in rootItems.DescendantsAndSelf)
                {
                    count++;
                }
            }
            catch
            {
                count = 0;
            }

            return count;
        }
    }
}