using System;
using System.Reflection;
using AIBot.Server;
using Xunit;

namespace AIBot.Tests
{
    /// <summary>可选 npcId 查询参数归一化：空串必须等价于缺省（不过滤），不能当过滤值或判非法。</summary>
    public class AdminOptionalFilterTests
    {
        private static string InvokeOptionalNpcId(string value)
        {
            var method = typeof(AdminEndpoints).GetMethod("OptionalNpcId",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);
            return (string)method.Invoke(null, new object[] { value });
        }

        [Fact]
        public void OptionalNpcId_NullStaysNull()
        {
            Assert.Null(InvokeOptionalNpcId(null));
        }

        [Fact]
        public void OptionalNpcId_EmptyStringBecomesNull()
        {
            Assert.Null(InvokeOptionalNpcId(""));
        }

        [Fact]
        public void OptionalNpcId_WhitespaceBecomesNull()
        {
            Assert.Null(InvokeOptionalNpcId("   "));
        }

        [Fact]
        public void OptionalNpcId_ValidIdPassesThrough()
        {
            Assert.Equal("herbalist_lin", InvokeOptionalNpcId("herbalist_lin"));
        }
    }
}
