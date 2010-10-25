#include "common.h"
#include "config.h"

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
					DS[i].offset = Buffer::read(&idx);
					DS[i].size = Buffer::read(&idx); 
					DS[i].unk3 = Buffer::read(&idx);
					DS[i].unk4 = Buffer::read(&idx);
					DS[i].unk5 = Buffer::read(&idx);
					DS[i].unk6 = Buffer::read(&idx);
					DS[i].unk7 = Buffer::read(&idx);
					DS[i].unk8 = Buffer::read(&idx);
					DS[i].unk9 = Buffer::read(&idx);
					DS[i].unk10 = Buffer::read(&idx);
				}

				for(unsigned int i = 0; i < Hdr.recordCount; i++)
				{
					/*
					type 1000056
					maybe Ressurectoin points with coords

					type 1010004
					FCTX files (maybe textures)
					*/
					if(DS[i].type == 1010041 && DS[i].size > 0 && (DS[i].unk0 & 0xff) < 36)
					{
						printf("%u - %u - %u\n%u - %u - %u - %u - %u - %u - %u - %u - %u - %u - %u\n", i, DS[i].idx, DS[i].type,
							DS[i].unk0, DS[i].offset, DS[i].size, DS[i].unk3, DS[i].unk4, DS[i].unk5, DS[i].unk6, DS[i].unk7, DS[i].unk8, DS[i].unk9, DS[i].unk10);
					}
					//*/
					if(DS[i].type == TEXTSCRIPT && textscript == true)
					{
						char* rdbnr = new char[3];
						sprintf(rdbnr, "%02i", (DS[i].unk0 & 0xff));
						if((DS[i].unk0 & 0xff) >=0 && (DS[i].unk0 & 0xff) < 36)
						{
							string rdbfile = "./RDB/";
							rdbfile += rdbnr;
							rdbfile+=".rdbdata";
							ifstream rdbf(rdbfile, ios::binary);
							if(rdbf)
							{
								string file = "./xml/textscript/RDB";
								file += rdbnr;
								file +="_";
								char* fnr = new char[14];
								sprintf(fnr, "%12i", DS[i].idx);
								file += fnr;
								file += ".xml";
								ofstream xmlout(file ,ios::out);
								if(xmlout)
								{
									rdbf.seekg(DS[i].offset);
									uint32 unknown1 = Buffer::read(&rdbf);
									uint32 unknown2 = Buffer::read(&rdbf);
									uint32 unknown3 = Buffer::read(&rdbf);
									string xmltest = Buffer::read(&rdbf, (DS[i].size - 12), false);
									xmlout << xmltest;
									
									rdbf.close();
								}
								
								xmlout.close();
							}
							else
							{
								printf("Error with file: %s\n", rdbfile.c_str());
							}
						}
					}
					if(DS[i].type == SCRIPTGROUP3 && scriptgroup == true)
					{
						char* rdbnr = new char[3];
						sprintf(rdbnr, "%02i", (DS[i].unk0 & 0xff));
						if((DS[i].unk0 & 0xff) >=0 && (DS[i].unk0 & 0xff) < 36)
						{
							string rdbfile = "./RDB/";
							rdbfile += rdbnr;
							rdbfile+=".rdbdata";
							ifstream rdbf(rdbfile, ios::binary);
							if(rdbf)
							{
								string file = "./xml/scriptgroup/RDB";
								file += rdbnr;
								file +="_";
								char* fnr = new char[10];
								sprintf(fnr, "%08i", i);
								file += fnr;
								file += ".xml";
								ofstream xmlout(file ,ios::out);
								if(xmlout)
								{
									rdbf.seekg(DS[i].offset);
									uint32 unknown1 = Buffer::read(&rdbf);
									uint32 unknown2 = Buffer::read(&rdbf);
									uint32 unknown3 = Buffer::read(&rdbf);
									string xmltest = Buffer::read(&rdbf, (DS[i].size - 12), false);
									xmlout << xmltest;
									
									rdbf.close();
								}
								
								xmlout.close();
							}
							else
							{
								printf("Error with file: %s\n", rdbfile.c_str());
							}
						}
					}
					if(DS[i].type == SCRIPTGROUP2 && scriptgroup == true)
					{
						char* rdbnr = new char[3];
						sprintf(rdbnr, "%02i", (DS[i].unk0 & 0xff));
						if((DS[i].unk0 & 0xff) >=0 && (DS[i].unk0 & 0xff) < 36)
						{
							string rdbfile = "./RDB/";
							rdbfile += rdbnr;
							rdbfile+=".rdbdata";
							ifstream rdbf(rdbfile, ios::binary);
							if(rdbf)
							{
								string file = "./xml/scriptgroup/RDB";
								file += rdbnr;
								file +="_";
								char* fnr = new char[10];
								sprintf(fnr, "%08i", i);
								file += fnr;
								file += ".xml";
								ofstream xmlout(file ,ios::out);
								if(xmlout)
								{
									rdbf.seekg(DS[i].offset);
									uint32 unknown1 = Buffer::read(&rdbf);
									uint32 unknown2 = Buffer::read(&rdbf);
									uint32 unknown3 = Buffer::read(&rdbf);
									string xmltest = Buffer::read(&rdbf, (DS[i].size - 12), false);
									xmlout << xmltest;
									
									rdbf.close();
								}
								
								xmlout.close();
							}
							else
							{
								printf("Error with file: %s\n", rdbfile.c_str());
							}
						}
					}
					if(DS[i].type == COLLECTIONLIBRARY && collectionlibrary == true)
					{
						char* rdbnr = new char[3];
						sprintf(rdbnr, "%02i", (DS[i].unk0 & 0xff));
						if((DS[i].unk0 & 0xff) >=0 && (DS[i].unk0 & 0xff) < 36)
						{
							string rdbfile = "./RDB/";
							rdbfile += rdbnr;
							rdbfile+=".rdbdata";
							ifstream rdbf(rdbfile, ios::binary);
							if(rdbf)
							{
								string file = "./xml/collectionlibrary/RDB";
								file += rdbnr;
								file +="_";
								char* fnr = new char[10];
								sprintf(fnr, "%08i", i);
								file += fnr;
								file += ".xml";
								ofstream xmlout(file ,ios::out);
								if(xmlout)
								{
									rdbf.seekg(DS[i].offset);
									uint32 unknown1 = Buffer::read(&rdbf);
									uint32 unknown2 = Buffer::read(&rdbf);
									uint32 unknown3 = Buffer::read(&rdbf);
									string xmltest = Buffer::read(&rdbf, (DS[i].size - 12), false);
									xmlout << xmltest;
									
									rdbf.close();
								}
								
								xmlout.close();
							}
							else
							{
								printf("Error with file: %s\n", rdbfile.c_str());
							}
						}
					}
					if(DS[i].type == STATICAIPOINTS2 && staticapoints2== true)
					{
						char* rdbnr = new char[3];
						sprintf(rdbnr, "%02i", (DS[i].unk0 & 0xff));
						if((DS[i].unk0 & 0xff) >=0 && (DS[i].unk0 & 0xff) < 36)
						{
							string rdbfile = "./RDB/";
							rdbfile += rdbnr;
							rdbfile+=".rdbdata";
							ifstream rdbf(rdbfile, ios::binary);
							if(rdbf)
							{
								string file = "./xml/staticapoints/RDB";
								file += rdbnr;
								file +="_";
								char* fnr = new char[10];
								sprintf(fnr, "%08i", i);
								file += fnr;
								file += ".xml";
								ofstream xmlout(file ,ios::out);
								if(xmlout)
								{
									rdbf.seekg(DS[i].offset);
									string xmltest = Buffer::read(&rdbf, (DS[i].size), false);
									xmlout << xmltest;
									
									rdbf.close();
								}
								
								xmlout.close();
							}
							else
							{
								printf("Error with file: %s\n", rdbfile.c_str());
							}
						}
					}
					if(DS[i].type == RIVERS && rivers == true)
					{
						char* rdbnr = new char[3];
						sprintf(rdbnr, "%02i", (DS[i].unk0 & 0xff));
						if((DS[i].unk0 & 0xff) >=0 && (DS[i].unk0 & 0xff) < 36)
						{
							string rdbfile = "./RDB/";
							rdbfile += rdbnr;
							rdbfile+=".rdbdata";
							ifstream rdbf(rdbfile, ios::binary);
							if(rdbf)
							{
								string file = "./xml/rivers/RDB";
								file += rdbnr;
								file +="_";
								char* fnr = new char[10];
								sprintf(fnr, "%08i", i);
								file += fnr;
								file += ".xml";
								ofstream xmlout(file ,ios::out);
								if(xmlout)
								{
									rdbf.seekg(DS[i].offset);
									uint32 unknown1 = Buffer::read(&rdbf);
									uint32 unknown2 = Buffer::read(&rdbf);
									uint32 unknown3 = Buffer::read(&rdbf);
									string xmltest = Buffer::read(&rdbf, (DS[i].size - 12), false);
									xmlout << xmltest;
									
									rdbf.close();
								}
								
								xmlout.close();
							}
							else
							{
								printf("Error with file: %s\n", rdbfile.c_str());
							}
						}
					}
					if(DS[i].type == PATROLS && patrols == true)
					{
						char* rdbnr = new char[3];
						sprintf(rdbnr, "%02i", (DS[i].unk0 & 0xff));
						if((DS[i].unk0 & 0xff) >=0 && (DS[i].unk0 & 0xff) < 36)
						{
							string rdbfile = "./RDB/";
							rdbfile += rdbnr;
							rdbfile+=".rdbdata";
							ifstream rdbf(rdbfile, ios::binary);
							if(rdbf)
							{
								string file = "./xml/patrols/RDB";
								file += rdbnr;
								file +="_";
								char* fnr = new char[10];
								sprintf(fnr, "%08i", i);
								file += fnr;
								file += ".xml";
								ofstream xmlout(file ,ios::out);
								if(xmlout)
								{
									rdbf.seekg(DS[i].offset);
									uint32 unknown1 = Buffer::read(&rdbf);
									uint32 unknown2 = Buffer::read(&rdbf);
									uint32 unknown3 = Buffer::read(&rdbf);
									string xmltest = Buffer::read(&rdbf, (DS[i].size - 12), false);
									xmlout << xmltest;
									//printf("%u - %u\n%u - %u - %u - %u - %u - %u - %u - %u - %u - %u - %u\n", DS[i].idx, DS[i].type,
									//	DS[i].unk0, DS[i].offset, DS[i].size, DS[i].unk3, DS[i].unk4, DS[i].unk5, DS[i].unk6, DS[i].unk7, DS[i].unk8, DS[i].unk9, DS[i].unk10);
									rdbf.close();
								}
								
								xmlout.close();
							}
							else
							{
								printf("Error with file: %s\n", rdbfile.c_str());
							}
						}
					}
					if(DS[i].type == BOUNDEDAREAS && boundedareas == true)
					{
						char* rdbnr = new char[3];
						sprintf(rdbnr, "%02i", (DS[i].unk0 & 0xff));
						if((DS[i].unk0 & 0xff) >=0 && (DS[i].unk0 & 0xff) < 36)
						{
							string rdbfile = "./RDB/";
							rdbfile += rdbnr;
							rdbfile+=".rdbdata";
							ifstream rdbf(rdbfile, ios::binary);
							if(rdbf)
							{
								string file = "./xml/boundedareas/RDB";
								file += rdbnr;
								file +="_";
								char* fnr = new char[10];
								sprintf(fnr, "%08i", i);
								file += fnr;
								file += ".xml";
								ofstream xmlout(file ,ios::out);
								if(xmlout)
								{
									rdbf.seekg(DS[i].offset);
									uint32 unknown1 = Buffer::read(&rdbf);
									uint32 unknown2 = Buffer::read(&rdbf);
									uint32 unknown3 = Buffer::read(&rdbf);
									string xmltest = Buffer::read(&rdbf, (DS[i].size - 12), false);
									xmlout << xmltest;
									//printf("%u - %u\n%u - %u - %u - %u - %u - %u - %u - %u - %u - %u - %u\n", DS[i].idx, DS[i].type,
									//	DS[i].unk0, DS[i].offset, DS[i].size, DS[i].unk3, DS[i].unk4, DS[i].unk5, DS[i].unk6, DS[i].unk7, DS[i].unk8, DS[i].unk9, DS[i].unk10);
									rdbf.close();
								}
								
								xmlout.close();
							}
							else
							{
								printf("Error with file: %s\n", rdbfile.c_str());
							}
						}
					}
					if(DS[i].type == STATICAIPOINTS && staticaipoints == true)
					{
						char* rdbnr = new char[3];
						sprintf(rdbnr, "%02i", (DS[i].unk0 & 0xff));
						if((DS[i].unk0 & 0xff) >=0 && (DS[i].unk0 & 0xff) < 36)
						{
							string rdbfile = "./RDB/";
							rdbfile += rdbnr;
							rdbfile+=".rdbdata";
							ifstream rdbf(rdbfile, ios::binary);
							if(rdbf)
							{
								string file = "./xml/staticaipoints/RDB";
								file += rdbnr;
								file +="_";
								char* fnr = new char[10];
								sprintf(fnr, "%08i", i);
								file += fnr;
								file += ".xml";
								ofstream xmlout(file ,ios::out);
								if(xmlout)
								{
									rdbf.seekg(DS[i].offset);
									uint32 unknown1 = Buffer::read(&rdbf);
									uint32 unknown2 = Buffer::read(&rdbf);
									uint32 unknown3 = Buffer::read(&rdbf);
									string xmltest = Buffer::read(&rdbf, (DS[i].size - 12), false);
									xmlout << xmltest;
									
									rdbf.close();
								}
								
								xmlout.close();
							}
							else
							{
								printf("Error with file: %s\n", rdbfile.c_str());
							}
						}
					}
					
					if(DS[i].type == PNG && exportPNG==true)
					{
						char* rdbnr = new char[3];
						sprintf(rdbnr, "%02i", (DS[i].unk0 & 0xff));
						if((DS[i].unk0 & 0xff) >=0 && (DS[i].unk0 & 0xff) < 36)
						{
							string rdbfile = "./RDB/";
							rdbfile += rdbnr;
							rdbfile+=".rdbdata";
							ifstream rdbf(rdbfile, ios::binary);
							if(rdbf)
							{
								string file = "./png/png/RDB";
								file += rdbnr;
								file +="_";
								char* fnr = new char[10];
								sprintf(fnr, "%08i", i);
								file += fnr;
								file += ".png";
								rdbf.seekg(DS[i].offset);
								uint32 unknown1 = Buffer::read(&rdbf);
								uint32 unknown2 = Buffer::read(&rdbf);
								uint32 unknown3 = Buffer::read(&rdbf);
									
								ofstream xmlout(file ,ios::binary);
								if(xmlout)
								{
									unsigned char k;
									for(unsigned l = 0; l < (DS[i].size - 12); l++)
									{
										k = rdbf.get() & 0xff;
										xmlout << k;
									}
								}
								else
								{
									printf("Error creating file: %s\n", file.c_str());
								}
								xmlout.close();
								rdbf.close();
							}
							else
							{
								printf("Error with file: %s\n", rdbfile.c_str());
							}
						}
					}
					if(DS[i].type == PNG2 && exportPNG2==true)
					{
						char* rdbnr = new char[3];
						sprintf(rdbnr, "%02i", (DS[i].unk0 & 0xff));
						if((DS[i].unk0 & 0xff) >=0 && (DS[i].unk0 & 0xff) < 36)
						{
							string rdbfile = "./RDB/";
							rdbfile += rdbnr;
							rdbfile+=".rdbdata";
							ifstream rdbf(rdbfile, ios::binary);
							if(rdbf)
							{
								string file = "./png/png2/RDB";
								file += rdbnr;
								file +="_";
								char* fnr = new char[10];
								sprintf(fnr, "%08i", i);
								file += fnr;
								file += ".png";
								rdbf.seekg(DS[i].offset);
								uint32 unknown1 = Buffer::read(&rdbf);
								uint32 unknown2 = Buffer::read(&rdbf);
								uint32 unknown3 = Buffer::read(&rdbf);
									
								ofstream xmlout(file ,ios::binary);
								if(xmlout)
								{
									unsigned char k;
									for(unsigned l = 0; l < (DS[i].size - 12); l++)
									{
										k = rdbf.get() & 0xff;
										xmlout << k;
									}
								}
								else
								{
									printf("Error creating file: %s\n", file.c_str());
								}
								xmlout.close();
								rdbf.close();
							}
							else
							{
								printf("Error with file: %s\n", rdbfile.c_str());
							}
						}
					}
					//*/
					if(DS[i].type == SCRIPTGROUP && scriptgroup == true)
					{
						char* rdbnr = new char[3];
						sprintf(rdbnr, "%02i", (DS[i].unk0 & 0xff));
						if((DS[i].unk0 & 0xff) >=0 && (DS[i].unk0 & 0xff) < 36)
						{
							string rdbfile = "./RDB/";
							rdbfile += rdbnr;
							rdbfile+=".rdbdata";
							ifstream rdbf(rdbfile, ios::binary);
							if(rdbf)
							{
								string file = "./xml/scriptgroup/RDB";
								file += rdbnr;
								file +="_";
								char* fnr = new char[10];
								sprintf(fnr, "%08i", i);
								file += fnr;
								file += ".xml";
								ofstream xmlout(file ,ios::out);

								rdbf.seekg(DS[i].offset);
								uint32 unknown1 = Buffer::read(&rdbf);
								uint32 unknown2 = Buffer::read(&rdbf);
								uint32 unknown3 = Buffer::read(&rdbf);
								string xmltest = Buffer::read(&rdbf, (DS[i].size - 12), false);
								xmlout << xmltest;
								
								rdbf.close();
								xmlout.close();
							}
							else
							{
								printf("Error with file: %s\n", rdbfile.c_str());
							}
						}
					}
					if(DS[i].type == ANIMSYS && animsys == true)
					{
						char* rdbnr = new char[3];
						sprintf(rdbnr, "%02i", (DS[i].unk0 & 0xff));
						if((DS[i].unk0 & 0xff) >=0 && (DS[i].unk0 & 0xff) < 36)
						{
							string rdbfile = "./RDB/";
							rdbfile += rdbnr;
							rdbfile+=".rdbdata";
							ifstream rdbf(rdbfile, ios::binary);
							if(rdbf)
							{
								string file = "./xml/animsys/RDB";
								file += rdbnr;
								file +="_";
								char* fnr = new char[10];
								sprintf(fnr, "%08i", i);
								file += fnr;
								file += ".xml";
								ofstream xmlout(file ,ios::out);

								rdbf.seekg(DS[i].offset);
								uint32 unknown1 = Buffer::read(&rdbf);
								uint32 unknown2 = Buffer::read(&rdbf);
								uint32 unknown3 = Buffer::read(&rdbf);
								string xmltest = Buffer::read(&rdbf, (DS[i].size - 12), false);
								xmlout << xmltest;
								
								rdbf.close();
								xmlout.close();
							}
							else
							{
								printf("Error with file: %s\n", rdbfile.c_str());
							}
						}
					}
				}

				/*
				ifstream rdb00("./RDB/13.rdbdata",ios::binary);
				ofstream xmlout("test.dxt",ios::out);
				streamoff offset = DS[274797].offset;
				rdb00.seekg(offset);
				string type = Buffer::read(&rdb00, 4, false);
				uint32 unk0 = Buffer::read(&rdb00);
				uint32 unk1 = Buffer::read(&rdb00);
				uint32 unk2 = Buffer::read(&rdb00);
				uint32 unk3 = Buffer::read(&rdb00);
				string type2 = Buffer::read(&rdb00, 4, false);
				string xmltest = Buffer::read(&rdb00, (DS[274797].size - 24), false);
				xmlout << xmltest;
				//*/
				
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