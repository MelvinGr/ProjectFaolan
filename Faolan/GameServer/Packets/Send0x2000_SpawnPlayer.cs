using Faolan.Core.Network;
using Faolan.Core.Network.Opcodes;

// ReSharper disable once CheckNamespace
namespace Faolan.GameServer
{
	public partial class GameServerListener
	{
		private static void SpawnPlayer(INetworkClient client)
		{
			var sender0 = new byte[] { 0x0D, 0x13, 0xCE, 0x71, 0xB1, 0x10, 0x63 };
			var receiver0 = new byte[] { 0x0D, 0x47, 0xC1, 0x67, 0x6C, 0x10, 0xBD, 0xB3, 0x82, 0x88, 0x01 };
			var packetData0_1 = new byte[]
			{
				0x14, 0x0D, 0x00, 0x00, 0x00, 0x00, 0x15, 0x44, 0x77, 0xF5, 0x3E, 0x1D, 0x00, 0x00, 0x00, 0x00, 0x25, 0x40, 0xA9, 0x60, 0x3F, 0x18, 0x8C, 0xE9, 0xD0, 0x01, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
				0x00, 0x00, 0x00, 0xAE, 0x89, 0x00, 0x00, 0x27, 0x21, 0x00, 0x00, 0x48, 0xA4, 0x00, 0xE9, 0x4D, 0xF1, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00,
				0x00, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x04, 0x2C, 0x00, 0x00, 0x00, 0x00, 0x6F, 0x00, 0x00,
				0x00, 0x00, 0x41, 0x70, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x23, 0x28, 0x00, 0x00, 0x00, 0x3C, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x07,
				0xE2, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x66, 0x7A, 0x00, 0x01, 0x90, 0x04, 0x00, 0x01, 0x94, 0xB4, 0x00, 0x01, 0x95, 0x7C, 0x00, 0x01, 0x91, 0x2D, 0x00, 0x01, 0x97, 0x0C, 0x00, 0x01, 0x95,
				0xDC, 0x00, 0x01, 0x91, 0xF4, 0x00, 0x01, 0x9C, 0x1C, 0x00, 0x01, 0x90, 0xCC, 0x00, 0x01, 0x90, 0x68, 0x00, 0x01, 0x96, 0x42, 0x00, 0x01, 0x92, 0x5A, 0x00, 0x01, 0x92, 0xBC, 0x00, 0x01, 0x91, 0x94, 0x00, 0x01,
				0x97, 0x70, 0x00, 0x01, 0x93, 0x22, 0x00, 0x01, 0x9A, 0x2C, 0x00, 0x01, 0x9C, 0x80, 0x00, 0x01, 0x95, 0x14, 0x00, 0x01, 0x97, 0xD4, 0x00, 0x01, 0x96, 0xA5, 0x00, 0x01, 0x93, 0xE9, 0x00, 0x01, 0x94, 0x4C, 0x00,
				0x01, 0x99, 0xC8, 0x00, 0x06, 0x43, 0xE9, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
				0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
				0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
				0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x41, 0x00, 0x00, 0x82, 0x08, 0x00, 0x00,

				0x08, 0x97, 0x0A,
			};

			var packetData0_2 = new byte[]
			{
				//0x08, 0x97, 0x0A,
				
				//0x06, 0x4D, 0x4D, 0x4D, 0x4D, 0x4D, 0x4D, 
				
				0x22, 0x48, 0x08, 0x02, 0x10, 0x00, 0x18, 0x03, 0x20, 0x66, 0x28, 0x00, 0x30, 0xAC, 0xBC, 0x0E, 0x38, 0xD4, 0xA7, 0x24, 0x40, 0x00, 0x48, 0x64, 0x50,
				0x88, 0xF7, 0x0F, 0x58, 0xD8, 0xCA, 0x23, 0x60, 0x00, 0x70, 0x64, 0x78, 0x00, 0x80, 0x01, 0x01, 0x88, 0x01, 0xE7, 0xB7, 0x30, 0x90, 0x01, 0x50, 0x98, 0x01, 0x50, 0xA0, 0x01, 0x64, 0xA8, 0x01, 0xC8, 0x61, 0xB0,
				0x01, 0x01, 0xD0, 0x01, 0x14, 0xE8, 0x01, 0x00, 0x80, 0x02, 0x3C, 0x90, 0x02, 0x01, 0x32, 0x13, 0x08, 0x01, 0x10, 0x91, 0x01, 0x18, 0xCC, 0x01, 0x40, 0x20, 0x58, 0x0A, 0x68, 0x81, 0x83, 0x06, 0x80, 0x01, 0x00,
				0x3A, 0x0D, 0x08, 0xA8, 0xC8, 0x02, 0x15, 0x00, 0x00, 0x00, 0x00, 0x18, 0x01, 0x50, 0x00, 0xA0, 0x01, 0x01, 0xA8, 0x01, 0x02, 0xBA, 0x01, 0x57, 0x08, 0x01, 0x12, 0x53, 0x0A, 0x14, 0x0D, 0xA9, 0xBB, 0xD8, 0xAA,
				0x15, 0x38, 0xC7, 0x89, 0xE4, 0x1D, 0x17, 0x04, 0x25, 0xAE, 0x25, 0x95, 0x48, 0x40, 0xCE, 0x12, 0x14, 0x0D, 0xA9, 0xBB, 0xD8, 0xAA, 0x15, 0x38, 0xC7, 0x89, 0xE4, 0x1D, 0x17, 0x04, 0x25, 0xAE, 0x25, 0x95, 0x48,
				0x40, 0xCE, 0x1A, 0x14, 0x0D, 0xA9, 0xBB, 0xD8, 0xAA, 0x15, 0x38, 0xC7, 0x89, 0xE4, 0x1D, 0x17, 0x04, 0x25, 0xAE, 0x25, 0x95, 0x48, 0x40, 0xCE, 0x25, 0x10, 0x10, 0x3C, 0x00, 0x2D, 0x10, 0x10, 0x3C, 0x00, 0x38,
				0x3D, 0x45, 0x53, 0x4D, 0x36, 0x58, 0xBA, 0x01, 0x57, 0x08, 0x02, 0x12, 0x53, 0x0A, 0x14, 0x0D, 0x00, 0xC9, 0x55, 0xF2, 0x15, 0x35, 0x08, 0xC1, 0x9B, 0x1D, 0xF8, 0x02, 0xFB, 0x7D, 0x25, 0xC4, 0xDD, 0x2E, 0xF2,
				0x12, 0x14, 0x0D, 0x00, 0xC9, 0x55, 0xF2, 0x15, 0x35, 0x08, 0xC1, 0x9B, 0x1D, 0xF8, 0x02, 0xFB, 0x7D, 0x25, 0xC4, 0xDD, 0x2E, 0xF2, 0x1A, 0x14, 0x0D, 0x00, 0xC9, 0x55, 0xF2, 0x15, 0x35, 0x08, 0xC1, 0x9B, 0x1D,
				0xF8, 0x02, 0xFB, 0x7D, 0x25, 0xC4, 0xDD, 0x2E, 0xF2, 0x25, 0x21, 0x99, 0x44, 0x00, 0x2D, 0x21, 0x99, 0x44, 0x00, 0x38, 0x50, 0x45, 0x37, 0x4E, 0x5A, 0x32, 0xBA, 0x01, 0x57, 0x08, 0x03, 0x12, 0x53, 0x0A, 0x14,
				0x0D, 0x92, 0x37, 0x35, 0x89, 0x15, 0x33, 0xB5, 0x64, 0x67, 0x1D, 0xB1, 0xC8, 0x99, 0xBA, 0x25, 0x38, 0x93, 0x29, 0xA1, 0x12, 0x14, 0x0D, 0x92, 0x37, 0x35, 0x89, 0x15, 0x33, 0xB5, 0x64, 0x67, 0x1D, 0xB1, 0xC8,
				0x99, 0xBA, 0x25, 0x38, 0x93, 0x29, 0xA1, 0x1A, 0x14, 0x0D, 0x92, 0x37, 0x35, 0x89, 0x15, 0x33, 0xB5, 0x64, 0x67, 0x1D, 0xB1, 0xC8, 0x99, 0xBA, 0x25, 0x38, 0x93, 0x29, 0xA1, 0x25, 0xCA, 0x2B, 0x40, 0x00, 0x2D,
				0xCA, 0x2B, 0x40, 0x00, 0x38, 0x01, 0x45, 0x51, 0x4D, 0x46, 0x4B, 0xBA, 0x01, 0x57, 0x08, 0x05, 0x12, 0x53, 0x0A, 0x14, 0x0D, 0x1C, 0x48, 0x2C, 0x22, 0x15, 0x8B, 0xAA, 0x58, 0x50, 0x1D, 0x88, 0x8A, 0xC9, 0x71,
				0x25, 0xD3, 0xD4, 0xD4, 0x5C, 0x12, 0x14, 0x0D, 0x1C, 0x48, 0x2C, 0x22, 0x15, 0x8B, 0xAA, 0x58, 0x50, 0x1D, 0x88, 0x8A, 0xC9, 0x71, 0x25, 0xD3, 0xD4, 0xD4, 0x5C, 0x1A, 0x14, 0x0D, 0x1C, 0x48, 0x2C, 0x22, 0x15,
				0x8B, 0xAA, 0x58, 0x50, 0x1D, 0x88, 0x8A, 0xC9, 0x71, 0x25, 0xD3, 0xD4, 0xD4, 0x5C, 0x25, 0x65, 0xAC, 0x44, 0x00, 0x2D, 0x65, 0xAC, 0x44, 0x00, 0x38, 0x50, 0x45, 0x4E, 0x48, 0x43, 0x34, 0xBA, 0x01, 0x57, 0x08,
				0x06, 0x12, 0x53, 0x0A, 0x14, 0x0D, 0x2D, 0xBF, 0x5C, 0x51, 0x15, 0xF4, 0x25, 0x44, 0xAE, 0x1D, 0x28, 0x19, 0x20, 0xCE, 0x25, 0x04, 0xC1, 0x13, 0x3D, 0x12, 0x14, 0x0D, 0x2D, 0xBF, 0x5C, 0x51, 0x15, 0xF4, 0x25,
				0x44, 0xAE, 0x1D, 0x28, 0x19, 0x20, 0xCE, 0x25, 0x04, 0xC1, 0x13, 0x3D, 0x1A, 0x14, 0x0D, 0x2D, 0xBF, 0x5C, 0x51, 0x15, 0xF4, 0x25, 0x44, 0xAE, 0x1D, 0x28, 0x19, 0x20, 0xCE, 0x25, 0x04, 0xC1, 0x13, 0x3D, 0x25,
				0xFA, 0x13, 0x3A, 0x00, 0x2D, 0xFA, 0x13, 0x3A, 0x00, 0x38, 0x50, 0x45, 0x33, 0x46, 0x52, 0x4C, 0xBA, 0x01, 0x57, 0x08, 0x07, 0x12, 0x53, 0x0A, 0x14, 0x0D, 0x2B, 0x71, 0x1D, 0x7C, 0x15, 0x68, 0xFC, 0x98, 0xAF,
				0x1D, 0xCA, 0x37, 0x5A, 0x3B, 0x25, 0xEF, 0x28, 0x64, 0xD0, 0x12, 0x14, 0x0D, 0x2B, 0x71, 0x1D, 0x7C, 0x15, 0x68, 0xFC, 0x98, 0xAF, 0x1D, 0xCA, 0x37, 0x5A, 0x3B, 0x25, 0xEF, 0x28, 0x64, 0xD0, 0x1A, 0x14, 0x0D,
				0x2B, 0x71, 0x1D, 0x7C, 0x15, 0x68, 0xFC, 0x98, 0xAF, 0x1D, 0xCA, 0x37, 0x5A, 0x3B, 0x25, 0xEF, 0x28, 0x64, 0xD0, 0x25, 0xE5, 0xAB, 0x44, 0x00, 0x2D, 0xE5, 0xAB, 0x44, 0x00, 0x38, 0x50, 0x45, 0x30, 0x4E, 0x35,
				0x56, 0xBA, 0x01, 0x57, 0x08, 0x09, 0x12, 0x53, 0x0A, 0x14, 0x0D, 0xAA, 0x35, 0xEC, 0x95, 0x15, 0x0C, 0xE6, 0xCC, 0xA7, 0x1D, 0x34, 0xB5, 0x98, 0x51, 0x25, 0x54, 0x56, 0x31, 0xB4, 0x12, 0x14, 0x0D, 0xAA, 0x35,
				0xEC, 0x95, 0x15, 0x0C, 0xE6, 0xCC, 0xA7, 0x1D, 0x34, 0xB5, 0x98, 0x51, 0x25, 0x54, 0x56, 0x31, 0xB4, 0x1A, 0x14, 0x0D, 0xAA, 0x35, 0xEC, 0x95, 0x15, 0x0C, 0xE6, 0xCC, 0xA7, 0x1D, 0x34, 0xB5, 0x98, 0x51, 0x25,
				0x54, 0x56, 0x31, 0xB4, 0x25, 0x4E, 0x98, 0x39, 0x00, 0x2D, 0x4E, 0x98, 0x39, 0x00, 0x38, 0x48, 0x45, 0x4F, 0x32, 0x4B, 0x50, 0xBA, 0x01, 0x57, 0x08, 0x0A, 0x12, 0x53, 0x0A, 0x14, 0x0D, 0x76, 0x74, 0xD4, 0x5D,
				0x15, 0xD9, 0x75, 0x8C, 0xBB, 0x1D, 0x5F, 0x57, 0x5F, 0xB5, 0x25, 0x99, 0x31, 0x4E, 0xB9, 0x12, 0x14, 0x0D, 0x76, 0x74, 0xD4, 0x5D, 0x15, 0xD9, 0x75, 0x8C, 0xBB, 0x1D, 0x5F, 0x57, 0x5F, 0xB5, 0x25, 0x99, 0x31,
				0x4E, 0xB9, 0x1A, 0x14, 0x0D, 0x76, 0x74, 0xD4, 0x5D, 0x15, 0xD9, 0x75, 0x8C, 0xBB, 0x1D, 0x5F, 0x57, 0x5F, 0xB5, 0x25, 0x99, 0x31, 0x4E, 0xB9, 0x25, 0x9A, 0xD0, 0x37, 0x00, 0x2D, 0x9A, 0xD0, 0x37, 0x00, 0x38,
				0x3C, 0x45, 0x4B, 0x59, 0x30, 0x4D, 0xBA, 0x01, 0x57, 0x08, 0x0B, 0x12, 0x53, 0x0A, 0x14, 0x0D, 0x0E, 0xA7, 0x41, 0x3F, 0x15, 0x3E, 0x80, 0x22, 0xBF, 0x1D, 0x0D, 0x53, 0x3E, 0xB6, 0x25, 0xE4, 0x2F, 0xE3, 0xE7,
				0x12, 0x14, 0x0D, 0x0E, 0xA7, 0x41, 0x3F, 0x15, 0x3E, 0x80, 0x22, 0xBF, 0x1D, 0x0D, 0x53, 0x3E, 0xB6, 0x25, 0xE4, 0x2F, 0xE3, 0xE7, 0x1A, 0x14, 0x0D, 0x0E, 0xA7, 0x41, 0x3F, 0x15, 0x3E, 0x80, 0x22, 0xBF, 0x1D,
				0x0D, 0x53, 0x3E, 0xB6, 0x25, 0xE4, 0x2F, 0xE3, 0xE7, 0x25, 0xD9, 0xA2, 0x3B, 0x00, 0x2D, 0xD9, 0xA2, 0x3B, 0x00, 0x38, 0x3F, 0x45, 0x49, 0x30, 0x42, 0x46, 0xBA, 0x01, 0x57, 0x08, 0x0C, 0x12, 0x53, 0x0A, 0x14,
				0x0D, 0x94, 0xA3, 0x50, 0x9F, 0x15, 0x1D, 0xF0, 0x8C, 0x0E, 0x1D, 0xEA, 0xBD, 0x23, 0xB6, 0x25, 0x62, 0xB4, 0xA4, 0x4B, 0x12, 0x14, 0x0D, 0x94, 0xA3, 0x50, 0x9F, 0x15, 0x1D, 0xF0, 0x8C, 0x0E, 0x1D, 0xEA, 0xBD,
				0x23, 0xB6, 0x25, 0x62, 0xB4, 0xA4, 0x4B, 0x1A, 0x14, 0x0D, 0x94, 0xA3, 0x50, 0x9F, 0x15, 0x1D, 0xF0, 0x8C, 0x0E, 0x1D, 0xEA, 0xBD, 0x23, 0xB6, 0x25, 0x62, 0xB4, 0xA4, 0x4B, 0x25, 0x62, 0x69, 0x39, 0x00, 0x2D,
				0x62, 0x69, 0x39, 0x00, 0x38, 0x50, 0x45, 0x4F, 0x39, 0x35, 0x59, 0xBA, 0x01, 0x57, 0x08, 0x0E, 0x12, 0x53, 0x0A, 0x14, 0x0D, 0x72, 0x95, 0x51, 0xAC, 0x15, 0xC2, 0x20, 0xFA, 0x89, 0x1D, 0x60, 0xD5, 0xE0, 0x46,
				0x25, 0xD0, 0x8D, 0x44, 0xCD, 0x12, 0x14, 0x0D, 0x72, 0x95, 0x51, 0xAC, 0x15, 0xC2, 0x20, 0xFA, 0x89, 0x1D, 0x60, 0xD5, 0xE0, 0x46, 0x25, 0xD0, 0x8D, 0x44, 0xCD, 0x1A, 0x14, 0x0D, 0x72, 0x95, 0x51, 0xAC, 0x15,
				0xC2, 0x20, 0xFA, 0x89, 0x1D, 0x60, 0xD5, 0xE0, 0x46, 0x25, 0xD0, 0x8D, 0x44, 0xCD, 0x25, 0xDE, 0xAB, 0x44, 0x00, 0x2D, 0xDE, 0xAB, 0x44, 0x00, 0x38, 0x50, 0x45, 0x58, 0x4D, 0x44, 0x52, 0xBA, 0x01, 0xB0, 0x01,
				0x08, 0x0F, 0x12, 0x53, 0x0A, 0x14, 0x0D, 0xFA, 0x47, 0x64, 0x64, 0x15, 0xD6, 0x6F, 0x47, 0xEC, 0x1D, 0x81, 0x7D, 0x17, 0x9F, 0x25, 0xE8, 0xC4, 0x08, 0xCC, 0x12, 0x14, 0x0D, 0xFA, 0x47, 0x64, 0x64, 0x15, 0xD6,
				0x6F, 0x47, 0xEC, 0x1D, 0x81, 0x7D, 0x17, 0x9F, 0x25, 0xE8, 0xC4, 0x08, 0xCC, 0x1A, 0x14, 0x0D, 0xFA, 0x47, 0x64, 0x64, 0x15, 0xD6, 0x6F, 0x47, 0xEC, 0x1D, 0x81, 0x7D, 0x17, 0x9F, 0x25, 0xE8, 0xC4, 0x08, 0xCC,
				0x25, 0x34, 0x5C, 0x03, 0x00, 0x2D, 0x34, 0x5C, 0x03, 0x00, 0x38, 0x50, 0x45, 0x49, 0x5A, 0x4A, 0x41, 0x1A, 0x57, 0x08, 0x00, 0x12, 0x53, 0x0A, 0x14, 0x0D, 0x06, 0xE5, 0x08, 0x76, 0x15, 0x1C, 0x2F, 0xAA, 0xD8,
				0x1D, 0x7B, 0x9D, 0x2C, 0x93, 0x25, 0x8D, 0xDD, 0xAD, 0x47, 0x12, 0x14, 0x0D, 0x63, 0x92, 0x87, 0x7F, 0x15, 0x4F, 0x07, 0x77, 0x2D, 0x1D, 0xEF, 0x68, 0x87, 0x0D, 0x25, 0xD6, 0x07, 0xB9, 0xB1, 0x1A, 0x14, 0x0D,
				0x06, 0xE5, 0x08, 0x76, 0x15, 0x1C, 0x2F, 0xAA, 0xD8, 0x1D, 0x7B, 0x9D, 0x2C, 0x93, 0x25, 0x8D, 0xDD, 0xAD, 0x47, 0x25, 0x40, 0xAE, 0x3C, 0x00, 0x2D, 0x41, 0xAE, 0x3C, 0x00, 0x38, 0x50, 0x45, 0x00, 0x00, 0x00,
				0x00, 0xBA, 0x01, 0xB0, 0x01, 0x08, 0x10, 0x12, 0x53, 0x0A, 0x14, 0x0D, 0x8D, 0xB1, 0xF9, 0xB1, 0x15, 0x22, 0xA1, 0x31, 0x34, 0x1D, 0x93, 0x50, 0xD6, 0xA2, 0x25, 0x0E, 0x5F, 0xB4, 0x97, 0x12, 0x14, 0x0D, 0x8D,
				0xB1, 0xF9, 0xB1, 0x15, 0x22, 0xA1, 0x31, 0x34, 0x1D, 0x93, 0x50, 0xD6, 0xA2, 0x25, 0x0E, 0x5F, 0xB4, 0x97, 0x1A, 0x14, 0x0D, 0x8D, 0xB1, 0xF9, 0xB1, 0x15, 0x22, 0xA1, 0x31, 0x34, 0x1D, 0x93, 0x50, 0xD6, 0xA2,
				0x25, 0x0E, 0x5F, 0xB4, 0x97, 0x25, 0x2B, 0x5C, 0x03, 0x00, 0x2D, 0x2B, 0x5C, 0x03, 0x00, 0x38, 0x50, 0x45, 0x45, 0x45, 0x41, 0x4E, 0x1A, 0x57, 0x08, 0x00, 0x12, 0x53, 0x0A, 0x14, 0x0D, 0x06, 0xE5, 0x08, 0x76,
				0x15, 0x1C, 0x2F, 0xAA, 0xD8, 0x1D, 0x7B, 0x9D, 0x2C, 0x93, 0x25, 0x8D, 0xDD, 0xAD, 0x47, 0x12, 0x14, 0x0D, 0x63, 0x92, 0x87, 0x7F, 0x15, 0x4F, 0x07, 0x77, 0x2D, 0x1D, 0xEF, 0x68, 0x87, 0x0D, 0x25, 0xD6, 0x07,
				0xB9, 0xB1, 0x1A, 0x14, 0x0D, 0x06, 0xE5, 0x08, 0x76, 0x15, 0x1C, 0x2F, 0xAA, 0xD8, 0x1D, 0x7B, 0x9D, 0x2C, 0x93, 0x25, 0x8D, 0xDD, 0xAD, 0x47, 0x25, 0x40, 0xAE, 0x3C, 0x00, 0x2D, 0x41, 0xAE, 0x3C, 0x00, 0x38,
				0x50, 0x45, 0x00, 0x00, 0x00, 0x00, 0xBA, 0x01, 0x57, 0x08, 0x12, 0x12, 0x53, 0x0A, 0x14, 0x0D, 0xC1, 0x0F, 0x82, 0xA2, 0x15, 0xBA, 0x3E, 0xCE, 0x2F, 0x1D, 0x26, 0x16, 0xD7, 0x5C, 0x25, 0x2A, 0xF7, 0x58, 0x47,
				0x12, 0x14, 0x0D, 0xC1, 0x0F, 0x82, 0xA2, 0x15, 0xBA, 0x3E, 0xCE, 0x2F, 0x1D, 0x26, 0x16, 0xD7, 0x5C, 0x25, 0x2A, 0xF7, 0x58, 0x47, 0x1A, 0x14, 0x0D, 0xC1, 0x0F, 0x82, 0xA2, 0x15, 0xBA, 0x3E, 0xCE, 0x2F, 0x1D,
				0x26, 0x16, 0xD7, 0x5C, 0x25, 0x2A, 0xF7, 0x58, 0x47, 0x25, 0x31, 0x99, 0x44, 0x00, 0x2D, 0x31, 0x99, 0x44, 0x00, 0x38, 0x50, 0x45, 0x45, 0x52, 0x4A, 0x54, 0xBA, 0x01, 0x57, 0x08, 0x14, 0x12, 0x53, 0x0A, 0x14,
				0x0D, 0xCF, 0xDF, 0x16, 0x54, 0x15, 0xF9, 0xF6, 0x76, 0xEE, 0x1D, 0xEE, 0x8A, 0x0A, 0xA3, 0x25, 0x5D, 0xD3, 0xFE, 0xE6, 0x12, 0x14, 0x0D, 0xCF, 0xDF, 0x16, 0x54, 0x15, 0xF9, 0xF6, 0x76, 0xEE, 0x1D, 0xEE, 0x8A,
				0x0A, 0xA3, 0x25, 0x5D, 0xD3, 0xFE, 0xE6, 0x1A, 0x14, 0x0D, 0xCF, 0xDF, 0x16, 0x54, 0x15, 0xF9, 0xF6, 0x76, 0xEE, 0x1D, 0xEE, 0x8A, 0x0A, 0xA3, 0x25, 0x5D, 0xD3, 0xFE, 0xE6, 0x25, 0xF5, 0x87, 0x10, 0x00, 0x2D,
				0xF5, 0x87, 0x10, 0x00, 0x38, 0x3A, 0x45, 0x44, 0x58, 0x50, 0x37, 0xBA, 0x01, 0x57, 0x08, 0x15, 0x12, 0x53, 0x0A, 0x14, 0x0D, 0x31, 0x6A, 0x7F, 0xCF, 0x15, 0xC3, 0x91, 0xDA, 0x49, 0x1D, 0xBD, 0x55, 0xAB, 0x97,
				0x25, 0xC0, 0xBE, 0x7C, 0x97, 0x12, 0x14, 0x0D, 0x31, 0x6A, 0x7F, 0xCF, 0x15, 0xC3, 0x91, 0xDA, 0x49, 0x1D, 0xBD, 0x55, 0xAB, 0x97, 0x25, 0xC0, 0xBE, 0x7C, 0x97, 0x1A, 0x14, 0x0D, 0x31, 0x6A, 0x7F, 0xCF, 0x15,
				0xC3, 0x91, 0xDA, 0x49, 0x1D, 0xBD, 0x55, 0xAB, 0x97, 0x25, 0xC0, 0xBE, 0x7C, 0x97, 0x25, 0x00, 0x90, 0x39, 0x00, 0x2D, 0x00, 0x90, 0x39, 0x00, 0x38, 0x50, 0x45, 0x4B, 0x42, 0x57, 0x30, 0xBA, 0x01, 0x58, 0x08,
				0xEA, 0x07, 0x12, 0x53, 0x0A, 0x14, 0x0D, 0xD2, 0x36, 0x9E, 0x14, 0x15, 0xE2, 0xD9, 0x90, 0x3B, 0x1D, 0x03, 0xD2, 0x05, 0xF6, 0x25, 0x45, 0xBF, 0x39, 0xCE, 0x12, 0x14, 0x0D, 0xD2, 0x36, 0x9E, 0x14, 0x15, 0xE2,
				0xD9, 0x90, 0x3B, 0x1D, 0x03, 0xD2, 0x05, 0xF6, 0x25, 0x45, 0xBF, 0x39, 0xCE, 0x1A, 0x14, 0x0D, 0xD2, 0x36, 0x9E, 0x14, 0x15, 0xE2, 0xD9, 0x90, 0x3B, 0x1D, 0x03, 0xD2, 0x05, 0xF6, 0x25, 0x45, 0xBF, 0x39, 0xCE,
				0x25, 0xC8, 0xDB, 0x47, 0x00, 0x2D, 0xC8, 0xDB, 0x47, 0x00, 0x38, 0x01, 0x45, 0x35, 0x56, 0x42, 0x33, 0xBA, 0x01, 0x58, 0x08, 0xEF, 0x07, 0x12, 0x53, 0x0A, 0x14, 0x0D, 0x07, 0xB2, 0xA5, 0x17, 0x15, 0xDF, 0xC4,
				0x30, 0x87, 0x1D, 0xCB, 0x1F, 0xB1, 0x7B, 0x25, 0xE4, 0x9D, 0xEA, 0x8F, 0x12, 0x14, 0x0D, 0x07, 0xB2, 0xA5, 0x17, 0x15, 0xDF, 0xC4, 0x30, 0x87, 0x1D, 0xCB, 0x1F, 0xB1, 0x7B, 0x25, 0xE4, 0x9D, 0xEA, 0x8F, 0x1A,
				0x14, 0x0D, 0x07, 0xB2, 0xA5, 0x17, 0x15, 0xDF, 0xC4, 0x30, 0x87, 0x1D, 0xCB, 0x1F, 0xB1, 0x7B, 0x25, 0xE4, 0x9D, 0xEA, 0x8F, 0x25, 0x7B, 0x98, 0x39, 0x00, 0x2D, 0x7B, 0x98, 0x39, 0x00, 0x38, 0x28, 0x45, 0x50,
				0x48, 0x51, 0x50, 0xBA, 0x01, 0x58, 0x08, 0xF3, 0x07, 0x12, 0x53, 0x0A, 0x14, 0x0D, 0xFA, 0x1E, 0xE0, 0xF9, 0x15, 0x76, 0x58, 0xF2, 0x2A, 0x1D, 0xA6, 0xD0, 0x84, 0x7C, 0x25, 0xCE, 0xCB, 0x66, 0x15, 0x12, 0x14,
				0x0D, 0xFA, 0x1E, 0xE0, 0xF9, 0x15, 0x76, 0x58, 0xF2, 0x2A, 0x1D, 0xA6, 0xD0, 0x84, 0x7C, 0x25, 0xCE, 0xCB, 0x66, 0x15, 0x1A, 0x14, 0x0D, 0xFA, 0x1E, 0xE0, 0xF9, 0x15, 0x76, 0x58, 0xF2, 0x2A, 0x1D, 0xA6, 0xD0,
				0x84, 0x7C, 0x25, 0xCE, 0xCB, 0x66, 0x15, 0x25, 0x12, 0x9B, 0x3B, 0x00, 0x2D, 0x12, 0x9B, 0x3B, 0x00, 0x38, 0x3E, 0x45, 0x39, 0x4A, 0x43, 0x52, 0xBA, 0x01, 0x58, 0x08, 0xF8, 0x07, 0x12, 0x53, 0x0A, 0x14, 0x0D,
				0x10, 0x74, 0x12, 0x6F, 0x15, 0x80, 0x45, 0xC0, 0x9E, 0x1D, 0x13, 0xDD, 0x93, 0x4F, 0x25, 0xEB, 0x02, 0x0C, 0xD1, 0x12, 0x14, 0x0D, 0x10, 0x74, 0x12, 0x6F, 0x15, 0x80, 0x45, 0xC0, 0x9E, 0x1D, 0x13, 0xDD, 0x93,
				0x4F, 0x25, 0xEB, 0x02, 0x0C, 0xD1, 0x1A, 0x14, 0x0D, 0x10, 0x74, 0x12, 0x6F, 0x15, 0x80, 0x45, 0xC0, 0x9E, 0x1D, 0x13, 0xDD, 0x93, 0x4F, 0x25, 0xEB, 0x02, 0x0C, 0xD1, 0x25, 0x83, 0x11, 0x3A, 0x00, 0x2D, 0x83,
				0x11, 0x3A, 0x00, 0x38, 0x01, 0x45, 0x34, 0x4E, 0x49, 0x34, 0xBA, 0x01, 0x58, 0x08, 0xFA, 0x07, 0x12, 0x53, 0x0A, 0x14, 0x0D, 0x9F, 0x54, 0xD6, 0xFF, 0x15, 0xFF, 0x0A, 0xE3, 0x04, 0x1D, 0x43, 0x71, 0x2A, 0xB1,
				0x25, 0x29, 0xF5, 0xBB, 0x53, 0x12, 0x14, 0x0D, 0x9F, 0x54, 0xD6, 0xFF, 0x15, 0xFF, 0x0A, 0xE3, 0x04, 0x1D, 0x43, 0x71, 0x2A, 0xB1, 0x25, 0x29, 0xF5, 0xBB, 0x53, 0x1A, 0x14, 0x0D, 0x9F, 0x54, 0xD6, 0xFF, 0x15,
				0xFF, 0x0A, 0xE3, 0x04, 0x1D, 0x43, 0x71, 0x2A, 0xB1, 0x25, 0x29, 0xF5, 0xBB, 0x53, 0x25, 0x54, 0xF3, 0x40, 0x00, 0x2D, 0x54, 0xF3, 0x40, 0x00, 0x38, 0x01, 0x45, 0x5A, 0x53, 0x43, 0x41, 0x00, 0x00, 0x00, 0x00,
				0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x03, 0xF1, 0x00, 0x00, 0x00, 0x0C, 0x00, 0x00, 0x00, 0xE3, 0x3F, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x00, 0x01, 0x0D, 0x3F,
				0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x05, 0x00, 0x00, 0x00, 0x00, 0x01, 0xCF, 0x3F, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x06, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0xBB, 0x44, 0x9B, 0xA6, 0x00, 0x00, 0x00, 0x07,
				0x00, 0x00, 0x00, 0x00, 0x01, 0x04, 0xBD, 0x03, 0x12, 0x6F, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00, 0x01, 0x02, 0xBC, 0x65, 0x60, 0x42, 0x00, 0x00, 0x00, 0x09, 0x00, 0x00, 0x00, 0x00, 0x01, 0x01, 0xBB,
				0x44, 0x9B, 0xA6, 0x00, 0x00, 0x00, 0x0A, 0x00, 0x00, 0x00, 0x00, 0x01, 0x06, 0xBB, 0x44, 0x9B, 0xA6, 0x00, 0x00, 0x00, 0x0B, 0x00, 0x00, 0x00, 0x00, 0x01, 0x03, 0xBD, 0x03, 0x12, 0x6F, 0x00, 0x00, 0x00, 0x0C,
				0x00, 0x00, 0x00, 0x00, 0x01, 0x05, 0xBB, 0x44, 0x9B, 0xA6, 0x00, 0x00, 0x00, 0x0D, 0x00, 0x00, 0x00, 0x00, 0x00, 0xA5, 0x3F, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x18, 0x00, 0x00, 0x00, 0x00, 0x02, 0xAD, 0x3F,
				0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x17, 0x00, 0x00, 0x00, 0x00, 0xC1, 0x21, 0x00, 0x00, 0x01, 0x90, 0x00, 0x00, 0x00, 0x11, 0x30, 0x00, 0x00, 0x00, 0x1B, 0x5A, 0x00, 0x00, 0x00, 0x22, 0xCD, 0x00, 0x00, 0x00,
				0x35, 0x23, 0x00, 0x00, 0x00, 0x48, 0x46, 0x00, 0x00, 0x01, 0x98, 0x83, 0x00, 0x00, 0x01, 0x98, 0x98, 0x00, 0x00, 0x04, 0x93, 0x7D, 0x0A, 0x00, 0x04, 0x99, 0x5B, 0x00, 0x00, 0x04, 0x9C, 0xDF, 0x00, 0x00, 0x04,
				0x9E, 0x0B, 0x00, 0x00, 0x04, 0x9F, 0xFF, 0x00, 0x00, 0x04, 0xA5, 0x77, 0x00, 0x00, 0x04, 0xA6, 0xA3, 0x00, 0x00, 0x04, 0xA7, 0x07, 0x00, 0x00, 0x04, 0xA7, 0xCE, 0x00, 0x00, 0x04, 0xA8, 0xFB, 0x00, 0x00, 0x04,
				0xAA, 0x26, 0x00, 0x00, 0x04, 0xAE, 0xD6, 0x00, 0x00, 0x04, 0xB2, 0x5A, 0x00, 0x00, 0x04, 0xB3, 0x86, 0x00, 0x00, 0x04, 0xB4, 0xB2, 0x00, 0x00, 0x04, 0xC9, 0x68, 0x00, 0x00, 0x04, 0xE8, 0xA5, 0x44, 0x00, 0x04,
				0xEB, 0x62, 0x43, 0x00, 0x04, 0xEE, 0x81, 0x4A, 0x00, 0x04, 0xEE, 0xE4, 0x4B, 0x00, 0x04, 0xEF, 0x4A, 0x46, 0x00, 0x04, 0xEF, 0xAD, 0x49, 0x00, 0x04, 0xF9, 0x70, 0x08, 0x00, 0x04, 0xFA, 0x9C, 0x08, 0x00, 0x04,
				0xFB, 0xC8, 0x08, 0x00, 0x04, 0xFE, 0x20, 0x47, 0x00, 0x04, 0xFF, 0x4C, 0x48, 0x00, 0x05, 0x04, 0xCC, 0x00, 0x00, 0x05, 0x06, 0xB8, 0x00, 0x00, 0x05, 0x0A, 0xA0, 0x00, 0x00, 0x05, 0x0B, 0x04, 0x0A, 0x00, 0x05,
				0x0E, 0x24, 0x00, 0x00, 0x15, 0x87, 0x54, 0x03, 0x00, 0x15, 0x88, 0xE4, 0x03, 0x00, 0x15, 0xF9, 0x00, 0x03, 0x00, 0x15, 0xFA, 0x90, 0x03, 0x00, 0x15, 0xFA, 0xF4, 0x03, 0x00, 0x15, 0xFB, 0xBC, 0x03, 0x00, 0x16,
				0x00, 0x6E, 0x03, 0x00, 0x16, 0x0C, 0x88, 0x00, 0x00, 0x00, 0x03, 0xF1, 0x00, 0x00, 0x00, 0x0B, 0x00, 0x00, 0x00, 0x0A, 0xFF, 0xFF, 0x39, 0xC2, 0x00, 0x00, 0x00, 0x14, 0xFF, 0xFF, 0xEF, 0x66, 0x00, 0x00, 0x00,
				0x1E, 0xFF, 0xFF, 0xD4, 0xDB, 0x00, 0x00, 0x00, 0x28, 0x00, 0x00, 0x2B, 0x25, 0x00, 0x00, 0x00, 0x32, 0xFF, 0xFD, 0xFF, 0xB7, 0x00, 0x00, 0x00, 0x3C, 0x00, 0x01, 0x29, 0x71, 0x00, 0x00, 0x00, 0x46, 0x00, 0x00,
				0x86, 0xE2, 0x00, 0x00, 0x00, 0x50, 0xFF, 0xFF, 0x78, 0xE2, 0x00, 0x00, 0x00, 0x5A, 0x00, 0x00, 0x37, 0x46, 0x00, 0x00, 0x00, 0x64, 0xFF, 0xFF, 0xC8, 0xBA, 0x00, 0x00, 0x00, 0x6E, 0x00, 0x06, 0xCA, 0x48, 0x00,
				0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x3C, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x6E, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x03, 0xF1
			};

			new PacketStream()
				.WriteHeader(sender0, receiver0, null, 0x00002000)
				.WriteArrayUInt32Length(new PacketStream()
					.WriteUInt32(GameServerOx2000RespondsOpcodes.Spawn)
					.WriteUInt32(0x0000C350)
					.WriteUInt32(client.Account.ClientInstance)
					.WriteUInt32(0x00000000)
					.WriteUInt32(0x2C0A0F0D) //0x2B0A0F0D)
					.WriteFloat(client.Character.Position.X)
					.WriteByte(0x15) // ??
					.WriteFloat(client.Character.Position.Y)
					.WriteByte(0x1d) // ??
					.WriteFloat(client.Character.Position.Z)
					.WriteByte(0x12) // ??
					.WriteArray(packetData0_1)
					/*.WriteUInt16((short)(0x0722 + client.Character.Name.Length))
					.WriteByte(0x0a)
					.WriteStringPrependLengthByte(client.Character.Name)*/
					.WriteStringByteLength("MMMMMM")//client.Character.Name)
					.WriteArray(packetData0_2))
				.Send(client);
		}

		private static void SpawnPlayerOLD(INetworkClient client)
		{
			var packetData1020 = new byte[]
			{
				0x14, 0x0D, 0x00, 0x00, 0x00, 0x00, 0x15, 0x44, 0x77, 0xF5, 0x3E, 0x1D, 0x00, 0x00, 0x00, 0x00, 0x25, 0x40, 0xA9, 0x60, 0x3F, 0x18, 0xBA, 0xEC, 0x43, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
				0x00, 0x00, 0x00, 0x00, 0x00, 0x05, 0xC9, 0xC4, 0x00, 0x00, 0x40, 0xA4, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0D, 0x00, 0x00, 0x00,
				0x0A, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x04, 0x29, 0x00, 0x00, 0x00, 0x00, 0x6F, 0x00, 0x00, 0x00,
				0x00, 0x41, 0x70, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x23, 0x28, 0x00, 0x00, 0x00, 0x0A, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x07, 0xE2, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
				0x00, 0x66, 0x7A, 0x00, 0x01, 0x38, 0x84, 0x00, 0x01,

				//////////////////////////////////
				// has to do with quests 

				0x43, 0x10, 0x00, 0x06, 0x43, 0xEC, 0x00, 0x01, 0x39, 0x48, 0x00, 0x01, 0x38, 0xE8, 0x00, 0x01, 0x3E, 0xC3, 0x00, 0x01, 0x48, 0x24, 0x00, 0x01, 0x48, 0x88, 0x00, 0x06, 0x42, 0x5C, 0x00, 0x01, 0x3F, 0x8C, 0x00,
				0x01, 0x43, 0x71, 0x00, 0x06, 0x41, 0xF8, 0x00, 0x01, 0x3F, 0x26, 0x00, 0x01, 0x3A, 0x11, 0x00, 0x01, 0x43, 0xD8, 0x00, 0x01, 0x45, 0xC9, 0x00, 0x01, 0x4E, 0xC4, 0x00, 0x06, 0x45, 0x15, 0x00, 0x01, 0x3B, 0xA0,
				0x00, 0x01, 0x3C, 0x06, 0x00, 0x01, 0x41, 0x1A, 0x00, 0x01, 0x44, 0x38, 0x00, 0x01, 0x4D, 0x98, 0x00, 0x01,

				//////////////////////////////////
				0x3C, 0x69, 0x00, 0x01, 0x42, 0x44, 0x41, 0x00, 0x00, 0x82, 0x08, 0x00, 0x00
			};

			var packetData1021 = new byte[]
			{
				0x22, 0x48, 0x08, 0x02, 0x10, 0x00, 0x18, 0x03, 0x20, 0x65, 0x28, 0x00, 0x30, 0xAC, 0xBC, 0x0E, 0x38, 0xF4, 0xD9, 0x26, 0x40, 0x00, 0x48, 0x64, 0x50, 0x88, 0xF7, 0x0F, 0x58, 0x8C, 0xE8, 0x27, 0x60, 0x00, 0x70,
				0x64, 0x78, 0x00, 0x80, 0x01, 0x01, 0x88, 0x01, 0xE8, 0xB7, 0x30, 0x90, 0x01, 0x50, 0x98, 0x01, 0x50, 0xA0, 0x01, 0x64, 0xA8, 0x01, 0xC8, 0x61, 0xB0, 0x01, 0x01, 0xD0, 0x01, 0x03, 0xE8, 0x01, 0x00, 0x90, 0x02,
				0x01, 0xA0, 0x02, 0x05, 0x32, 0x0C, 0x08, 0x01, 0x10, 0xEF, 0x01, 0x18, 0x89, 0x04, 0x40, 0x20, 0x58, 0x0A, 0x3A, 0x0D, 0x08, 0xA8, 0xC8, 0x02, 0x15, 0x00, 0x00, 0x00, 0x00, 0x18, 0x01, 0x50, 0x00, 0xA0, 0x01,
				0x01, 0xA8, 0x01, 0x02, 0xBA, 0x01, 0x57, 0x08, 0x01, 0x12, 0x53, 0x0A, 0x14, 0x0D, 0x9B, 0x7E, 0x2C, 0xB3, 0x15, 0x3E, 0x34, 0xD2, 0x97, 0x1D, 0x9C, 0x68, 0xBB, 0xB3, 0x25, 0x5D, 0xA4, 0xF0, 0x97, 0x12, 0x14,
				0x0D, 0x9B, 0x7E, 0x2C, 0xB3, 0x15, 0x3E, 0x34, 0xD2, 0x97, 0x1D, 0x9C, 0x68, 0xBB, 0xB3, 0x25, 0x5D, 0xA4, 0xF0, 0x97, 0x1A, 0x14, 0x0D, 0x9B, 0x7E, 0x2C, 0xB3, 0x15, 0x3E, 0x34, 0xD2, 0x97, 0x1D, 0x9C, 0x68,
				0xBB, 0xB3, 0x25, 0x5D, 0xA4, 0xF0, 0x97, 0x25, 0x69, 0xA8, 0x39, 0x00, 0x2D, 0x69, 0xA8, 0x39, 0x00, 0x38, 0x50, 0x45, 0x4F, 0x56, 0x55, 0x51, 0xBA, 0x01, 0x57, 0x08, 0x02, 0x12, 0x53, 0x0A, 0x14, 0x0D, 0x22,
				0x0A, 0xC6, 0x0B, 0x15, 0x05, 0x7E, 0x88, 0x21, 0x1D, 0xC1, 0xA6, 0xF7, 0x0E, 0x25, 0x53, 0x80, 0x11, 0x5D, 0x12, 0x14, 0x0D, 0x22, 0x0A, 0xC6, 0x0B, 0x15, 0x05, 0x7E, 0x88, 0x21, 0x1D, 0xC1, 0xA6, 0xF7, 0x0E,
				0x25, 0x53, 0x80, 0x11, 0x5D, 0x1A, 0x14, 0x0D, 0x22, 0x0A, 0xC6, 0x0B, 0x15, 0x05, 0x7E, 0x88, 0x21, 0x1D, 0xC1, 0xA6, 0xF7, 0x0E, 0x25, 0x53, 0x80, 0x11, 0x5D, 0x25, 0x38, 0x5C, 0x03, 0x00, 0x2D, 0x38, 0x5C,
				0x03, 0x00, 0x38, 0x50, 0x45, 0x5A, 0x35, 0x55, 0x33, 0xBA, 0x01, 0x57, 0x08, 0x03, 0x12, 0x53, 0x0A, 0x14, 0x0D, 0x1E, 0x92, 0x4F, 0x72, 0x15, 0x46, 0x6A, 0x9E, 0x58, 0x1D, 0xB2, 0xC0, 0x1C, 0x17, 0x25, 0x13,
				0xF6, 0x21, 0x7D, 0x12, 0x14, 0x0D, 0x1E, 0x92, 0x4F, 0x72, 0x15, 0x46, 0x6A, 0x9E, 0x58, 0x1D, 0xB2, 0xC0, 0x1C, 0x17, 0x25, 0x13, 0xF6, 0x21, 0x7D, 0x1A, 0x14, 0x0D, 0x1E, 0x92, 0x4F, 0x72, 0x15, 0x46, 0x6A,
				0x9E, 0x58, 0x1D, 0xB2, 0xC0, 0x1C, 0x17, 0x25, 0x13, 0xF6, 0x21, 0x7D, 0x25, 0x1B, 0x98, 0x03, 0x00, 0x2D, 0x1B, 0x98, 0x03, 0x00, 0x38, 0x01, 0x45, 0x51, 0x56, 0x55, 0x48, 0xBA, 0x01, 0x57, 0x08, 0x05, 0x12,
				0x53, 0x0A, 0x14, 0x0D, 0x16, 0x46, 0x29, 0x35, 0x15, 0xC2, 0xAB, 0xA3, 0xD0, 0x1D, 0x77, 0x91, 0x91, 0x4B, 0x25, 0xE1, 0xAE, 0xE7, 0x8B, 0x12, 0x14, 0x0D, 0x16, 0x46, 0x29, 0x35, 0x15, 0xC2, 0xAB, 0xA3, 0xD0,
				0x1D, 0x77, 0x91, 0x91, 0x4B, 0x25, 0xE1, 0xAE, 0xE7, 0x8B, 0x1A, 0x14, 0x0D, 0x16, 0x46, 0x29, 0x35, 0x15, 0xC2, 0xAB, 0xA3, 0xD0, 0x1D, 0x77, 0x91, 0x91, 0x4B, 0x25, 0xE1, 0xAE, 0xE7, 0x8B, 0x25, 0x19, 0x6B,
				0x39, 0x00, 0x2D, 0x19, 0x6B, 0x39, 0x00, 0x38, 0x50, 0x45, 0x42, 0x51, 0x37, 0x30, 0xBA, 0x01, 0x57, 0x08, 0x06, 0x12, 0x53, 0x0A, 0x14, 0x0D, 0x09, 0xEF, 0x28, 0xA8, 0x15, 0x66, 0xE4, 0x0C, 0x94, 0x1D, 0x9F,
				0x1B, 0x14, 0xDE, 0x25, 0x63, 0x3E, 0x6E, 0x63, 0x12, 0x14, 0x0D, 0x09, 0xEF, 0x28, 0xA8, 0x15, 0x66, 0xE4, 0x0C, 0x94, 0x1D, 0x9F, 0x1B, 0x14, 0xDE, 0x25, 0x63, 0x3E, 0x6E, 0x63, 0x1A, 0x14, 0x0D, 0x09, 0xEF,
				0x28, 0xA8, 0x15, 0x66, 0xE4, 0x0C, 0x94, 0x1D, 0x9F, 0x1B, 0x14, 0xDE, 0x25, 0x63, 0x3E, 0x6E, 0x63, 0x25, 0x81, 0x08, 0x3A, 0x00, 0x2D, 0x81, 0x08, 0x3A, 0x00, 0x38, 0x50, 0x45, 0x4E, 0x30, 0x48, 0x4F, 0xBA,
				0x01, 0x57, 0x08, 0x07, 0x12, 0x53, 0x0A, 0x14, 0x0D, 0x8E, 0x26, 0xD5, 0xDB, 0x15, 0xDE, 0x8E, 0xA2, 0xD0, 0x1D, 0xC3, 0x84, 0x5B, 0x10, 0x25, 0x9D, 0x89, 0x37, 0x06, 0x12, 0x14, 0x0D, 0x8E, 0x26, 0xD5, 0xDB,
				0x15, 0xDE, 0x8E, 0xA2, 0xD0, 0x1D, 0xC3, 0x84, 0x5B, 0x10, 0x25, 0x9D, 0x89, 0x37, 0x06, 0x1A, 0x14, 0x0D, 0x8E, 0x26, 0xD5, 0xDB, 0x15, 0xDE, 0x8E, 0xA2, 0xD0, 0x1D, 0xC3, 0x84, 0x5B, 0x10, 0x25, 0x9D, 0x89,
				0x37, 0x06, 0x25, 0x0E, 0x6B, 0x39, 0x00, 0x2D, 0x0E, 0x6B, 0x39, 0x00, 0x38, 0x50, 0x45, 0x58, 0x47, 0x57, 0x37, 0xBA, 0x01, 0x57, 0x08, 0x09, 0x12, 0x53, 0x0A, 0x14, 0x0D, 0xAA, 0x35, 0xEC, 0x95, 0x15, 0x0C,
				0xE6, 0xCC, 0xA7, 0x1D, 0x34, 0xB5, 0x98, 0x51, 0x25, 0x54, 0x56, 0x31, 0xB4, 0x12, 0x14, 0x0D, 0xAA, 0x35, 0xEC, 0x95, 0x15, 0x0C, 0xE6, 0xCC, 0xA7, 0x1D, 0x34, 0xB5, 0x98, 0x51, 0x25, 0x54, 0x56, 0x31, 0xB4,
				0x1A, 0x14, 0x0D, 0xAA, 0x35, 0xEC, 0x95, 0x15, 0x0C, 0xE6, 0xCC, 0xA7, 0x1D, 0x34, 0xB5, 0x98, 0x51, 0x25, 0x54, 0x56, 0x31, 0xB4, 0x25, 0x4E, 0x98, 0x39, 0x00, 0x2D, 0x4E, 0x98, 0x39, 0x00, 0x38, 0x48, 0x45,
				0x4F, 0x32, 0x4B, 0x50, 0xBA, 0x01, 0x57, 0x08, 0x0A, 0x12, 0x53, 0x0A, 0x14, 0x0D, 0x76, 0x74, 0xD4, 0x5D, 0x15, 0xD9, 0x75, 0x8C, 0xBB, 0x1D, 0x5F, 0x57, 0x5F, 0xB5, 0x25, 0x99, 0x31, 0x4E, 0xB9, 0x12, 0x14,
				0x0D, 0x76, 0x74, 0xD4, 0x5D, 0x15, 0xD9, 0x75, 0x8C, 0xBB, 0x1D, 0x5F, 0x57, 0x5F, 0xB5, 0x25, 0x99, 0x31, 0x4E, 0xB9, 0x1A, 0x14, 0x0D, 0x76, 0x74, 0xD4, 0x5D, 0x15, 0xD9, 0x75, 0x8C, 0xBB, 0x1D, 0x5F, 0x57,
				0x5F, 0xB5, 0x25, 0x99, 0x31, 0x4E, 0xB9, 0x25, 0x9A, 0xD0, 0x37, 0x00, 0x2D, 0x9A, 0xD0, 0x37, 0x00, 0x38, 0x3C, 0x45, 0x4B, 0x59, 0x30, 0x4D, 0xBA, 0x01, 0x57, 0x08, 0x0B, 0x12, 0x53, 0x0A, 0x14, 0x0D, 0x0E,
				0xA7, 0x41, 0x3F, 0x15, 0x3E, 0x80, 0x22, 0xBF, 0x1D, 0x0D, 0x53, 0x3E, 0xB6, 0x25, 0xE4, 0x2F, 0xE3, 0xE7, 0x12, 0x14, 0x0D, 0x0E, 0xA7, 0x41, 0x3F, 0x15, 0x3E, 0x80, 0x22, 0xBF, 0x1D, 0x0D, 0x53, 0x3E, 0xB6,
				0x25, 0xE4, 0x2F, 0xE3, 0xE7, 0x1A, 0x14, 0x0D, 0x0E, 0xA7, 0x41, 0x3F, 0x15, 0x3E, 0x80, 0x22, 0xBF, 0x1D, 0x0D, 0x53, 0x3E, 0xB6, 0x25, 0xE4, 0x2F, 0xE3, 0xE7, 0x25, 0xD9, 0xA2, 0x3B, 0x00, 0x2D, 0xD9, 0xA2,
				0x3B, 0x00, 0x38, 0x3F, 0x45, 0x49, 0x30, 0x42, 0x46, 0xBA, 0x01, 0x57, 0x08, 0x0C, 0x12, 0x53, 0x0A, 0x14, 0x0D, 0x56, 0x9F, 0x2E, 0xAC, 0x15, 0x53, 0x10, 0x21, 0x26, 0x1D, 0x15, 0xE1, 0x0B, 0x39, 0x25, 0x21,
				0xAF, 0xDA, 0x86, 0x12, 0x14, 0x0D, 0x56, 0x9F, 0x2E, 0xAC, 0x15, 0x53, 0x10, 0x21, 0x26, 0x1D, 0x15, 0xE1, 0x0B, 0x39, 0x25, 0x21, 0xAF, 0xDA, 0x86, 0x1A, 0x14, 0x0D, 0x56, 0x9F, 0x2E, 0xAC, 0x15, 0x53, 0x10,
				0x21, 0x26, 0x1D, 0x15, 0xE1, 0x0B, 0x39, 0x25, 0x21, 0xAF, 0xDA, 0x86, 0x25, 0x30, 0x5C, 0x03, 0x00, 0x2D, 0x30, 0x5C, 0x03, 0x00, 0x38, 0x50, 0x45, 0x51, 0x57, 0x34, 0x39, 0xBA, 0x01, 0x57, 0x08, 0x0E, 0x12,
				0x53, 0x0A, 0x14, 0x0D, 0x24, 0xBC, 0x8F, 0x70, 0x15, 0xFB, 0xCB, 0x09, 0x5A, 0x1D, 0xC7, 0x8C, 0xC8, 0x07, 0x25, 0x7D, 0xD2, 0x28, 0xC6, 0x12, 0x14, 0x0D, 0x24, 0xBC, 0x8F, 0x70, 0x15, 0xFB, 0xCB, 0x09, 0x5A,
				0x1D, 0xC7, 0x8C, 0xC8, 0x07, 0x25, 0x7D, 0xD2, 0x28, 0xC6, 0x1A, 0x14, 0x0D, 0x24, 0xBC, 0x8F, 0x70, 0x15, 0xFB, 0xCB, 0x09, 0x5A, 0x1D, 0xC7, 0x8C, 0xC8, 0x07, 0x25, 0x7D, 0xD2, 0x28, 0xC6, 0x25, 0x3A, 0xFD,
				0x39, 0x00, 0x2D, 0x3A, 0xFD, 0x39, 0x00, 0x38, 0x50, 0x45, 0x46, 0x38, 0x43, 0x5A, 0xBA, 0x01, 0x57, 0x08, 0x0F, 0x12, 0x53, 0x0A, 0x14, 0x0D, 0xC9, 0x13, 0xAB, 0x43, 0x15, 0x0D, 0x49, 0xC0, 0x1D, 0x1D, 0x33,
				0xBE, 0xF5, 0xCD, 0x25, 0x5A, 0x29, 0x89, 0xA7, 0x12, 0x14, 0x0D, 0xC9, 0x13, 0xAB, 0x43, 0x15, 0x0D, 0x49, 0xC0, 0x1D, 0x1D, 0x33, 0xBE, 0xF5, 0xCD, 0x25, 0x5A, 0x29, 0x89, 0xA7, 0x1A, 0x14, 0x0D, 0xC9, 0x13,
				0xAB, 0x43, 0x15, 0x0D, 0x49, 0xC0, 0x1D, 0x1D, 0x33, 0xBE, 0xF5, 0xCD, 0x25, 0x5A, 0x29, 0x89, 0xA7, 0x25, 0x75, 0x6B, 0x39, 0x00, 0x2D, 0x75, 0x6B, 0x39, 0x00, 0x38, 0x50, 0x45, 0x56, 0x58, 0x47, 0x43, 0xBA,
				0x01, 0x57, 0x08, 0x10, 0x12, 0x53, 0x0A, 0x14, 0x0D, 0x41, 0x29, 0x8D, 0xBD, 0x15, 0x0D, 0x48, 0xE1, 0x1D, 0x1D, 0x4F, 0x29, 0xB3, 0x5E, 0x25, 0x2C, 0xB0, 0x07, 0xA8, 0x12, 0x14, 0x0D, 0x41, 0x29, 0x8D, 0xBD,
				0x15, 0x0D, 0x48, 0xE1, 0x1D, 0x1D, 0x4F, 0x29, 0xB3, 0x5E, 0x25, 0x2C, 0xB0, 0x07, 0xA8, 0x1A, 0x14, 0x0D, 0x41, 0x29, 0x8D, 0xBD, 0x15, 0x0D, 0x48, 0xE1, 0x1D, 0x1D, 0x4F, 0x29, 0xB3, 0x5E, 0x25, 0x2C, 0xB0,
				0x07, 0xA8, 0x25, 0x24, 0xFD, 0x39, 0x00, 0x2D, 0x24, 0xFD, 0x39, 0x00, 0x38, 0x50, 0x45, 0x38, 0x47, 0x4B, 0x47, 0xBA, 0x01, 0x57, 0x08, 0x12, 0x12, 0x53, 0x0A, 0x14, 0x0D, 0xDD, 0x31, 0xF2, 0x6E, 0x15, 0x11,
				0x83, 0x8B, 0x64, 0x1D, 0x7E, 0xFD, 0xAD, 0xA2, 0x25, 0x89, 0xAC, 0xB2, 0xA9, 0x12, 0x14, 0x0D, 0xDD, 0x31, 0xF2, 0x6E, 0x15, 0x11, 0x83, 0x8B, 0x64, 0x1D, 0x7E, 0xFD, 0xAD, 0xA2, 0x25, 0x89, 0xAC, 0xB2, 0xA9,
				0x1A, 0x14, 0x0D, 0xDD, 0x31, 0xF2, 0x6E, 0x15, 0x11, 0x83, 0x8B, 0x64, 0x1D, 0x7E, 0xFD, 0xAD, 0xA2, 0x25, 0x89, 0xAC, 0xB2, 0xA9, 0x25, 0x2C, 0x5C, 0x03, 0x00, 0x2D, 0x2C, 0x5C, 0x03, 0x00, 0x38, 0x50, 0x45,
				0x59, 0x5A, 0x4A, 0x32, 0xBA, 0x01, 0x58, 0x08, 0xEA, 0x07, 0x12, 0x53, 0x0A, 0x14, 0x0D, 0xF3, 0xF5, 0x13, 0x94, 0x15, 0xBB, 0x07, 0x2A, 0x8A, 0x1D, 0x48, 0x5C, 0xC3, 0x32, 0x25, 0xB5, 0x34, 0x41, 0x75, 0x12,
				0x14, 0x0D, 0xF3, 0xF5, 0x13, 0x94, 0x15, 0xBB, 0x07, 0x2A, 0x8A, 0x1D, 0x48, 0x5C, 0xC3, 0x32, 0x25, 0xB5, 0x34, 0x41, 0x75, 0x1A, 0x14, 0x0D, 0xF3, 0xF5, 0x13, 0x94, 0x15, 0xBB, 0x07, 0x2A, 0x8A, 0x1D, 0x48,
				0x5C, 0xC3, 0x32, 0x25, 0xB5, 0x34, 0x41, 0x75, 0x25, 0xAD, 0x6B, 0x38, 0x00, 0x2D, 0xAD, 0x6B, 0x38, 0x00, 0x38, 0x12, 0x45, 0x4A, 0x49, 0x56, 0x35, 0xBA, 0x01, 0x58, 0x08, 0xEF, 0x07, 0x12, 0x53, 0x0A, 0x14,
				0x0D, 0x2D, 0x59, 0x43, 0xD0, 0x15, 0x61, 0xC1, 0xFE, 0xB1, 0x1D, 0x63, 0x1C, 0x46, 0x9F, 0x25, 0x7A, 0xAC, 0xDF, 0xAF, 0x12, 0x14, 0x0D, 0x2D, 0x59, 0x43, 0xD0, 0x15, 0x61, 0xC1, 0xFE, 0xB1, 0x1D, 0x63, 0x1C,
				0x46, 0x9F, 0x25, 0x7A, 0xAC, 0xDF, 0xAF, 0x1A, 0x14, 0x0D, 0x2D, 0x59, 0x43, 0xD0, 0x15, 0x61, 0xC1, 0xFE, 0xB1, 0x1D, 0x63, 0x1C, 0x46, 0x9F, 0x25, 0x7A, 0xAC, 0xDF, 0xAF, 0x25, 0x4D, 0xF1, 0x37, 0x00, 0x2D,
				0x4D, 0xF1, 0x37, 0x00, 0x38, 0x01, 0x45, 0x58, 0x55, 0x54, 0x43, 0xBA, 0x01, 0x58, 0x08, 0xF3, 0x07, 0x12, 0x53, 0x0A, 0x14, 0x0D, 0xF5, 0xC9, 0xD6, 0x9F, 0x15, 0xAA, 0xDE, 0x30, 0x02, 0x1D, 0x87, 0x65, 0xF5,
				0xA6, 0x25, 0xB9, 0x5C, 0x84, 0x8B, 0x12, 0x14, 0x0D, 0xF5, 0xC9, 0xD6, 0x9F, 0x15, 0xAA, 0xDE, 0x30, 0x02, 0x1D, 0x87, 0x65, 0xF5, 0xA6, 0x25, 0xB9, 0x5C, 0x84, 0x8B, 0x1A, 0x14, 0x0D, 0xF5, 0xC9, 0xD6, 0x9F,
				0x15, 0xAA, 0xDE, 0x30, 0x02, 0x1D, 0x87, 0x65, 0xF5, 0xA6, 0x25, 0xB9, 0x5C, 0x84, 0x8B, 0x25, 0x2F, 0xB3, 0x38, 0x00, 0x2D, 0x2F, 0xB3, 0x38, 0x00, 0x38, 0x50, 0x45, 0x52, 0x4A, 0x5A, 0x36, 0xBA, 0x01, 0x58,
				0x08, 0xF6, 0x07, 0x12, 0x53, 0x0A, 0x14, 0x0D, 0xAF, 0xA2, 0x8A, 0x94, 0x15, 0x8B, 0x59, 0x17, 0x6C, 0x1D, 0x1A, 0xDB, 0x45, 0x8D, 0x25, 0x77, 0xE0, 0xC4, 0xC5, 0x12, 0x14, 0x0D, 0xAF, 0xA2, 0x8A, 0x94, 0x15,
				0x8B, 0x59, 0x17, 0x6C, 0x1D, 0x1A, 0xDB, 0x45, 0x8D, 0x25, 0x77, 0xE0, 0xC4, 0xC5, 0x1A, 0x14, 0x0D, 0xAF, 0xA2, 0x8A, 0x94, 0x15, 0x8B, 0x59, 0x17, 0x6C, 0x1D, 0x1A, 0xDB, 0x45, 0x8D, 0x25, 0x77, 0xE0, 0xC4,
				0xC5, 0x25, 0x09, 0xDF, 0x4B, 0x00, 0x2D, 0x09, 0xDF, 0x4B, 0x00, 0x38, 0x01, 0x45, 0x36, 0x41, 0x49, 0x32, 0xBA, 0x01, 0x58, 0x08, 0xFA, 0x07, 0x12, 0x53, 0x0A, 0x14, 0x0D, 0x9F, 0x54, 0xD6, 0xFF, 0x15, 0xFF,
				0x0A, 0xE3, 0x04, 0x1D, 0x43, 0x71, 0x2A, 0xB1, 0x25, 0x29, 0xF5, 0xBB, 0x53, 0x12, 0x14, 0x0D, 0x9F, 0x54, 0xD6, 0xFF, 0x15, 0xFF, 0x0A, 0xE3, 0x04, 0x1D, 0x43, 0x71, 0x2A, 0xB1, 0x25, 0x29, 0xF5, 0xBB, 0x53,
				0x1A, 0x14, 0x0D, 0x9F, 0x54, 0xD6, 0xFF, 0x15, 0xFF, 0x0A, 0xE3, 0x04, 0x1D, 0x43, 0x71, 0x2A, 0xB1, 0x25, 0x29, 0xF5, 0xBB, 0x53, 0x25, 0x54, 0xF3, 0x40, 0x00, 0x2D, 0x54, 0xF3, 0x40, 0x00, 0x38, 0x01, 0x45,
				0x5A, 0x53, 0x43, 0x41, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x03, 0xF1, 0x00, 0x00, 0x00, 0x0E, 0x00, 0x00, 0x02, 0xB6, 0x3F, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00,
				0x02, 0x00, 0x00, 0x00, 0x00, 0x01, 0x27, 0x3F, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01, 0x85, 0x3F, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0xE4,
				0x3F, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x05, 0x00, 0x00, 0x00, 0x00, 0x01, 0x0F, 0x3F, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x06, 0x00, 0x00, 0x00, 0x00, 0x01, 0xD0, 0x3F, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00,
				0x07, 0x00, 0x00, 0x00, 0x00, 0x01, 0xE4, 0x3F, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x3C, 0x83, 0x12, 0x6F, 0x00, 0x00, 0x00, 0x09, 0x00, 0x00, 0x00, 0x00, 0x01, 0x04,
				0x3C, 0x93, 0x74, 0xBC, 0x00, 0x00, 0x00, 0x0A, 0x00, 0x00, 0x00, 0x00, 0x01, 0x02, 0x3D, 0x2C, 0x08, 0x31, 0x00, 0x00, 0x00, 0x0B, 0x00, 0x00, 0x00, 0x00, 0x01, 0x01, 0x3C, 0x83, 0x12, 0x6F, 0x00, 0x00, 0x00,
				0x0C, 0x00, 0x00, 0x00, 0x00, 0x01, 0x06, 0x3C, 0x83, 0x12, 0x6F, 0x00, 0x00, 0x00, 0x0D, 0x00, 0x00, 0x00, 0x00, 0x01, 0x03, 0x3C, 0x93, 0x74, 0xBC, 0x00, 0x00, 0x00, 0x0E, 0x00, 0x00, 0x00, 0x00, 0x01, 0x05,
				0x3C, 0x83, 0x12, 0x6F, 0x00, 0x00, 0x00, 0x0F, 0x00, 0x00, 0x00, 0x00, 0xAD, 0x6C, 0x00, 0x00, 0x01, 0x90, 0x00, 0x00, 0x00, 0x10, 0x04, 0x00, 0x00, 0x00, 0x1B, 0x5A, 0x00, 0x00, 0x00, 0x22, 0xCD, 0x00, 0x00,
				0x00, 0x35, 0x23, 0x00, 0x00, 0x00, 0x48, 0x46, 0x00, 0x00, 0x01, 0x4D, 0x1F, 0x00, 0x00, 0x01, 0x50, 0xB8, 0x00, 0x00, 0x01, 0x51, 0xEB, 0x00, 0x00, 0x04, 0x93, 0x7D, 0x0A, 0x00, 0x04, 0x99, 0x5B, 0x00, 0x00,
				0x04, 0x9C, 0xDF, 0x00, 0x00, 0x04, 0x9E, 0x0B, 0x00, 0x00, 0x04, 0x9F, 0xFF, 0x00, 0x00, 0x04, 0xA5, 0x77, 0x00, 0x00, 0x04, 0xA6, 0xA3, 0x00, 0x00, 0x04, 0xA7, 0x07, 0x00, 0x00, 0x04, 0xA7, 0xCE, 0x00, 0x00,
				0x04, 0xA8, 0xFB, 0x00, 0x00, 0x04, 0xAA, 0x26, 0x00, 0x00, 0x04, 0xAE, 0xD6, 0x00, 0x00, 0x04, 0xB2, 0x5A, 0x00, 0x00, 0x04, 0xB3, 0x86, 0x00, 0x00, 0x04, 0xB4, 0xB2, 0x00, 0x00, 0x04, 0xC7, 0x10, 0x00, 0x00,
				0x04, 0xE6, 0xB2, 0x41, 0x00, 0x04, 0xE7, 0xDE, 0x42, 0x00, 0x04, 0xEE, 0x85, 0x4A, 0x00, 0x04, 0xEE, 0xE9, 0x4B, 0x00, 0x04, 0xEF, 0x4D, 0x46, 0x00, 0x04, 0xEF, 0xB1, 0x49, 0x00, 0x04, 0xF9, 0x70, 0x08, 0x00,
				0x04, 0xFA, 0x9C, 0x08, 0x00, 0x04, 0xFB, 0xC8, 0x08, 0x00, 0x04, 0xFE, 0xEA, 0x47, 0x00, 0x04, 0xFF, 0x51, 0x48, 0x00, 0x05, 0x04, 0xCC, 0x00, 0x00, 0x05, 0x06, 0xB8, 0x00, 0x00, 0x05, 0x0A, 0xA0, 0x00, 0x00,
				0x05, 0x0B, 0x04, 0x0A, 0x00, 0x05, 0x0E, 0x24, 0x00, 0x00, 0x15, 0xFA, 0xF4, 0x03, 0x00, 0x16, 0x0C, 0x88, 0x00, 0x00, 0x00, 0x03, 0xF1, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0x0A, 0xFF, 0xFF, 0xC7, 0xD9,
				0x00, 0x00, 0x00, 0x14, 0x00, 0x00, 0x38, 0x27, 0x00, 0x00, 0x00, 0x1E, 0x00, 0x00, 0x27, 0x29, 0x00, 0x00, 0x00, 0x28, 0xFF, 0xFF, 0xD8, 0xD7, 0x00, 0x00, 0x00, 0x32, 0xFF, 0xFF, 0x9D, 0x13, 0x00, 0x00, 0x00,
				0x3C, 0x00, 0x00, 0x62, 0xED, 0x00, 0x00, 0x00, 0x5A, 0x00, 0x00, 0x1F, 0x8B, 0x00, 0x00, 0x00, 0x64, 0xFF, 0xFF, 0xE0, 0x75, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0xF1
			};

			new PacketStream()
				.WriteHeader(Sender0, Receiver0, null, 0x2000)
				.WriteArrayUInt32Length(new PacketStream()
					.WriteUInt32(GameServerOx2000RespondsOpcodes.Spawn)
					.WriteUInt32(0x0000C350)
					.WriteUInt32(client.Account.ClientInstance)
					.WriteUInt32(0x00000000)
					.WriteUInt32(0x2B0A0F0D)
					.WriteFloat(client.Character.Position.X)
					.WriteByte(0x15) // ??
					.WriteFloat(client.Character.Position.Y)
					.WriteByte(0x1d) // ??
					.WriteFloat(client.Character.Position.Z)
					.WriteByte(0x12) // ??
					.WriteArray(packetData1020)
					.WriteUInt16((short)(0x0722 + client.Character.Name.Length))
					.WriteByte(0x0a)
					.WriteStringByteLength(client.Character.Name)
					.WriteArray(packetData1021))
				.Send(client);

			/*new PacketStream()
			    .WriteHeader(Sender0, Receiver0, null, 0x2000)
			    .WriteArrayPrependLengthUInt32(new PacketStream()
			        .WriteUInt32(GameServerOx2000RespondsOpcodes.Spawn)
			        .WriteUInt32(0x0000C350)
			        .WriteUInt32(client.Account.ClientInstance)
			        .WriteArray(packetData1020)
			        .WriteArrayPrependLengthUInt16(new PacketStream()
			            .WriteByte(0x0a)
			            .WriteShortString(client.Character.Name)
			            .WriteArray(packetData1021), true))
			    .Send(client);*/
		}
	}
}
