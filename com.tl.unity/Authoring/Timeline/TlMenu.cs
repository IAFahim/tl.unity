namespace Tl.Unity
{
    public static class TlMenu
    {
        public static string ShortName(string menu)
        {
            if (string.IsNullOrEmpty(menu))
                return "Tl";
            var slash = menu.LastIndexOf('/');
            return slash < 0 ? menu : menu.Substring(slash + 1);
        }
    }
}
