struct TypId
{
	uint32 idx;
	uint32 type;

	uint32 unk0;
	uint32 unk1;
	uint32 unk2;
	uint32 unk3;
	uint32 unk4;
	uint32 unk5;
	uint32 unk6;
	uint32 unk7;
	uint32 unk8;
	uint32 unk9;
	uint32 unk10;
};

struct Header
{
	string magic;
	uint32 version;
	uint32 hash1;
	uint32 hash2;
	uint32 hash3;
	uint32 hash4;
	uint32 recordCount;
};