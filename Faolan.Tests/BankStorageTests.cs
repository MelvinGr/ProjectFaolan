using System;
using Faolan.Core;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Economy;
using Faolan.Core.Protobuf;
using Xunit;

namespace Faolan.Tests
{
	// Golden-byte test for the BankIIR (0x0BA1C1CB) framing that delivers the personal bank container (kind 105),
	// decoded from NecroL80_mage-necromancer_login_Has-inventory-at-trader-bank (memory protocol_bank_storage).
	// Proves BankCodec reproduces the captured on-wire bank record byte-for-byte given the captured
	// ProtoInventoryGame blob: [subop 0BA1C1CB][type C350][charInstance][flag 01][u32 BE blobLen][blob], with the
	// 0x2000 packet length prefix (WriteArrayUInt32Length).
	public class BankStorageTests
	{
		// The full captured BankIIR 0x2000 record body (NecroL80, 9 bank items).
		private const string CapturedBankBody = "000004920ba1c1cb0000c3500802e5d401000004810a07086910d4cb8b401032181e227a0800127608c194e19c818080ea20" +
		                                        "1207080010818080201206081710a0943c1204081a10011a530a140dec662fc115a26bd85c1d31597189257d4344d212140d" +
		                                        "ec662fc115a26bd85c1d31597189257d4344d21a140dec662fc115a26bd85c1d31597189257d4344d2258cf037002d8cf037" +
		                                        "0038504544494b52228601080112810108a8c7ffff9fcad0eb20120708001081808020120608171098943c1204081a100112" +
		                                        "0908890210f9daad8c051a530a140d1b7b20eb1523e9e77d1d5bb874ce25f7dd1d6d12140d1b7b20eb1523e9e77d1d5bb874" +
		                                        "ce25f7dd1d6d1a140d1b7b20eb1523e9e77d1d5bb874ce25f7dd1d6d25ee513a002dee513a00381a4551304739227a080212" +
		                                        "7608a7cb88fccb8080ea20120708001081808020120608171099943c1204081a10011a530a140d575bcd73152f3cc4801d25" +
		                                        "63c51425914d94a812140d575bcd73152f3cc4801d2563c51425914d94a81a140d575bcd73152f3cc4801d2563c51425914d" +
		                                        "94a825a1f240002da1f24000380145494c3157228701080312820108f6dba193828080ea201208080810ffffffff0f120608" +
		                                        "17109a943c1204081a10011209088902109be8bbcc041a530a140de4ca9f1f15c03a1a941deff812d4254016c03f12140de4" +
		                                        "ca9f1f15c03a1a941deff812d4254016c03f1a140de4ca9f1f15c03a1a941deff812d4254016c03f25ee6231002dee623100" +
		                                        "38014532583543227a0804127608bbb08cb6818080ea2012070800108180806012060817109b943c1204081a10011a530a14" +
		                                        "0d56c71c58151113b8121db531e6a725789a538a12140d56c71c58151113b8121db531e6a725789a538a1a140d56c71c5815" +
		                                        "1113b8121db531e6a725789a538a2548ee0a002d48ee0a0038014534365450227a080a127608b69ff793ac8080ea20120708" +
		                                        "00108180802012060817109c943c1204081a10011a530a140d154a4f3215f3b9b2361dcd8f5eed25a1918dbe12140d154a4f" +
		                                        "3215f3b9b2361dcd8f5eed25a1918dbe1a140d154a4f3215f3b9b2361dcd8f5eed25a1918dbe2531c23d002d31c23d00384e" +
		                                        "45455a4c4b2277080b127308c8dcbbc0db8080ea2012040800100112060817109d943c1204081a10081a530a140db6784f1f" +
		                                        "156fe4c2941d5215e8e72586729b2a12140db6784f1f156fe4c2941d5215e8e72586729b2a1a140db6784f1f156fe4c2941d" +
		                                        "5215e8e72586729b2a25315711002d315711003801453649494c227a080c1276089abf88fccb8080ea201207080010818080" +
		                                        "2012060817109e943c1204081a10011a530a140dd1386134150df9190f1d9f95d630257bd1ec2f12140dd1386134150df919" +
		                                        "0f1d9f95d630257bd1ec2f1a140dd1386134150df9190f1d9f95d630257bd1ec2f259ef240002d9ef2400038014544563632" +
		                                        "227a080d12760898bf88fccb8080ea2012070800108180802012060817109f943c1204081a10011a530a140d89a8ad121583" +
		                                        "d4bebd1dc577e12825f1f3027a12140d89a8ad121583d4bebd1dc577e12825f1f3027a1a140d89a8ad121583d4bebd1dc577" +
		                                        "e12825f1f3027a2559f340002d59f3400038014548413034";

		private const uint CharInstance = 0x0802E5D4;

		[Fact]
		public void BankCodec_Matches_CapturedBank()
		{
			var captured = Convert.FromHexString(CapturedBankBody);

			// Header layout: [u32 recLen][u32 subop][u32 type][u32 instance][u8 flag][u32 blobLen][blob].
			// The blob (the ProtoInventoryGame) starts after recLen(4)+subop(4)+type(4)+instance(4)+flag(1)+blobLen(4)=21.
			var blob = captured[21..];

			var record = BankCodec.Build(new BankIIR(CharInstance, blob.FromProtobuf<ProtoInventoryGame>()));

			// Re-add the 0x2000 packet length prefix the send path writes (WriteArrayUInt32Length).
			var s = new FaolanStream();
			s.WriteArrayUInt32Length(record);

			Assert.Equal(Convert.ToHexStringLower(captured), Convert.ToHexStringLower(s.ToArray()));

			// Rule 1: the record Parse returns is the record Build takes, so the captured bytes rebuild themselves.
			Assert.Equal(Convert.ToHexStringLower(record), Convert.ToHexStringLower(BankCodec.Build(BankCodec.Parse(record))));
		}
	}
}
