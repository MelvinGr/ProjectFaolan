#include "common.h"

using namespace std;

int main()
{
	printf("ProjectFaolan - RDB Reader %s\n", VERSION);

	ifstream idx("./RDB/le.idx",ios::binary);

	if(idx)
	{
		vector<TypId> DS;
		Header Hdr;
		Hdr.version = 0;
		Hdr.hash1 = 0;
		Hdr.hash2 = 0;
		Hdr.hash3 = 0;
		Hdr.hash4 = 0;
		Hdr.recordCount = 0;
		uint32 pointer = 0;
		
		Hdr.magic = Buffer::read(&idx, 4);
		printf("Get Header: %s\n", Hdr.magic.c_str());
		Hdr.version = Buffer::read(&idx);
		printf("Version: %i\n", Hdr.version);
		
		if(Hdr.version == 5)
		{
			Hdr.hash1 = Buffer::read(&idx);
			Hdr.hash2 = Buffer::read(&idx);
			Hdr.hash3 = Buffer::read(&idx);
			Hdr.hash4 = Buffer::read(&idx);
			Hdr.recordCount = Buffer::read(&idx);
			if(Hdr.recordCount > 0)
			{
				printf("%i records found\n", Hdr.recordCount);
				for(unsigned int i = 0; i < Hdr.recordCount; i++)
				{
					TypId tmp;
					tmp.type = Buffer::read(&idx);
					tmp.idx = Buffer::read(&idx);
					DS.push_back(tmp);
				}
				for(unsigned int i = 0; i < Hdr.recordCount; i++)
				{
					DS[i].unk0 = Buffer::read(&idx);
					DS[i].unk1 = Buffer::read(&idx);
					DS[i].unk2 = Buffer::read(&idx); //maybe size
					DS[i].unk3 = Buffer::read(&idx);
					DS[i].unk4 = Buffer::read(&idx);
					DS[i].unk5 = Buffer::read(&idx);
					DS[i].unk6 = Buffer::read(&idx);
					DS[i].unk7 = Buffer::read(&idx);
					DS[i].unk8 = Buffer::read(&idx);
					DS[i].unk9 = Buffer::read(&idx);
					DS[i].unk10 = Buffer::read(&idx);

					if(DS[i].unk0 > 0x3fffecc0 && DS[i].unk0 < 0x3fffed00)
					{
					printf("unk0 found: %u - %u\n%u - %u - %u - %u - %u - %u - %u - %u - %u - %u - %u\n", DS[i].idx, DS[i].type,
						DS[i].unk0, DS[i].unk1, DS[i].unk2, DS[i].unk3, DS[i].unk4, DS[i].unk5, DS[i].unk6, DS[i].unk7, DS[i].unk8, DS[i].unk9, DS[i].unk10);
					}
					if(DS[i].unk1 > 0x3fffecc0 && DS[i].unk1 < 0x3fffed00)
					{
					printf("unk1 found: %u - %u\n%u - Offset: %u - %u - %u - %u - %u - %u - %u - %u - %u - %u\n", DS[i].idx, DS[i].type,
						DS[i].unk0, DS[i].unk1, DS[i].unk2, DS[i].unk3, DS[i].unk4, DS[i].unk5, DS[i].unk6, DS[i].unk7, DS[i].unk8, DS[i].unk9, DS[i].unk10);
					printf("Found in i: %i\n", i);
					}
				}
				ifstream rdb00("./RDB/00.rdbdata",ios::binary);
				ofstream xmlout("xmltest.xml",ios::out);
				streamoff offset = DS[324955].unk1;
				rdb00.seekg(offset);
				uint32 unk0 = Buffer::read(&rdb00);
				uint32 unk1 = Buffer::read(&rdb00);
				uint32 unk2 = Buffer::read(&rdb00);
				string xmltest = Buffer::read(&rdb00, (DS[324955].unk2 - 12), false);
				printf("%02x %02x %02x %02x\n", xmltest.c_str()[0], xmltest.c_str()[1], xmltest.c_str()[2], xmltest.c_str()[3]);
				xmlout << xmltest;
				
			}
			else
			{
				printf("Got wrong recordCounter\n");
			}
		}
		
		printf("END\n");
	}
	else
	{
		printf("Can't open idx file!!!\n");
	}
	_getch();
}