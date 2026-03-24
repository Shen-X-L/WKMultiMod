using System.Net.NetworkInformation;
using WKMPMod.Core;
using WKMPMod.Data;
using WKMPMod.Util;

namespace test {
    public class Tests {
        [SetUp]
        public void Setup() {
        }

        [Test]
        public void Test1() {
            Assert.Pass();
        }

		[Test]
		public void Test_SetField_ChangesStatus() {
			// Arrange
			MPStatus myStatus = MPStatus.NotInitialized;

			// Act
			// 注意:这里必须使用 ref,否则 myStatus 永远是 NotInitialized
			myStatus.SetField(MPStatus.INIT_MASK, MPStatus.Initialized);
			myStatus.SetField(MPStatus.LOBBY_MASK, MPStatus.InLobby);

			// Assert
			Assert.That(myStatus.IsInitialized(), Is.True, "应该标记为已初始化");
			Assert.That(myStatus.GetField(MPStatus.LOBBY_MASK), Is.EqualTo(MPStatus.InLobby), "大厅状态应该是 InLobby");
		}

		[Test]
		public void TestSystemLanguage() {
			Assert.That(Localization.GetGameLanguage, Is.EqualTo("zh"), "本地语言应为zh");
		}

		[Test]
		public void TestDataWriter() {
			DataWriter writer = new DataWriter();
			int? a = 1;
			writer.Put(a);
			writer.Put(1);
		}
	}
}