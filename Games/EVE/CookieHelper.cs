using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using Newtonsoft.Json;

namespace ISBoxerEVELauncher.Games.EVE {
    public static class CookieHelper {
        public static List<Cookie> GetAllCookies(CookieContainer cookieContainer) {
            var cookies = new List<Cookie>();

            // 利用 CookieContainer 自带的 GetCookies，但需要知道所有域名
            // 通过反射读取 m_domainTable
            var tableField = cookieContainer.GetType().GetField(
                "m_domainTable",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (tableField == null) return cookies;

            var table = tableField.GetValue(cookieContainer) as System.Collections.IDictionary;
            if (table == null) return cookies;

            foreach (var key in table.Keys) {
                var domain = key as string;
                if (domain == null) continue;

                var domainEntry = table[key];
                if (domainEntry == null) continue;

                // PathList 内部有一个 SortedList，其值是 CookieCollection
                // 尝试找到包含 CookieCollection 的字段或属性
                var entryType = domainEntry.GetType();

                // .NET 4.x: PathList 有 m_list (SortedList<string, CookieCollection>)
                System.Reflection.FieldInfo listField = null;
                foreach (var f in entryType.GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)) {
                    if (typeof(System.Collections.IDictionary).IsAssignableFrom(f.FieldType)
                        || typeof(System.Collections.IEnumerable).IsAssignableFrom(f.FieldType)) {
                        listField = f;
                        break;
                    }
                }

                if (listField != null) {
                    var innerList = listField.GetValue(domainEntry) as System.Collections.IDictionary;
                    if (innerList != null) {
                        foreach (var val in innerList.Values) {
                            var col = val as CookieCollection;
                            if (col != null) {
                                foreach (Cookie c in col) cookies.Add(c);
                            }
                        }
                        continue;
                    }
                    // 可能是 SortedList (non-generic)
                    var sortedList = listField.GetValue(domainEntry) as System.Collections.IEnumerable;
                    if (sortedList != null) {
                        foreach (var item in sortedList) {
                            var col = item as CookieCollection;
                            if (col != null)
                                foreach (Cookie c in col) cookies.Add(c);
                        }
                        continue;
                    }
                }

                // 兜底：反射找所有 CookieCollection 类型的属性/字段
                foreach (var prop in entryType.GetProperties(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)) {
                    if (prop.PropertyType == typeof(CookieCollection)) {
                        try {
                            var col = prop.GetValue(domainEntry, null) as CookieCollection;
                            if (col != null) foreach (Cookie c in col) cookies.Add(c);
                        } catch { }
                    }
                }
            }
            return cookies;
        }

        // 把 CookieContainer 转为 Base64 字符串
        public static string ToBase64(CookieContainer container) {
            try {
                var list = GetAllCookies(container);
                var json = JsonConvert.SerializeObject(list);
                return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
            }
            catch {
                return string.Empty;
            }
        }

        // 从 Base64 字符串还原 List<Cookie>
        public static List<Cookie> FromBase64(string base64) {
            try {
                var json = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
                return JsonConvert.DeserializeObject<List<Cookie>>(json) ?? new List<Cookie>();
            }
            catch {
                return new List<Cookie>();
            }
        }

        // 从 Cookie 列表创建 CookieContainer
        public static CookieContainer CreateContainer(List<Cookie> cookies) {
            var container = new CookieContainer();
            if (cookies != null) {
                foreach (var c in cookies) {
                    try {
                        container.Add(c);
                    }
                    catch { /* 某些 Cookie 可能无效 */ }
                }
            }
            return container;
        }
    }
}
