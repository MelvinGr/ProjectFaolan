#pragma comment( lib, "zlib.lib" )

#include <windows.h>
#include <iostream>
#include <string>
#include <fstream>
#include <sstream>
#include <conio.h>
#include <zlib.h>
#define client "peer0_"
#define server "peer1_"

using namespace std;
void extract(string * data);
void DoAll();
void decompressClient(unsigned char data[500000], unsigned long length);
void decompressServer(unsigned char data[500000], unsigned long length);
char GetDigitValue(char digit);

bool exitProg = false;

unsigned long buf_len;
	unsigned long buf_len2;
	z_stream* stream = new z_stream();
	int err;
	char Cdest[500000];
	char Cdest2[500000];
	int lendest = 500000;
	bool first_pack=true;
	bool first_pack2=true;

	z_stream* stream2 = new z_stream();
int err2;

unsigned long serverCount = 0;
unsigned long clientCount = 0;

int main()
{
	DoAll();
	printf("End\n");
	getch();
	return 0 ;
}

void extract(string *data)
{
	string clientx;
	string hash;
	size_t header = 0 ,fk = 0 ,sk = 0, tk = 0 , endPart = 0;
	int i = 0 ;
	while(!exitProg)
	{
		header = data->find("char peer", header);

		if(header!=string::npos)
		{
			hash = data->substr(header+9, 1);
			//sk = data->find("{", header);
			//fk = data->find("}", header);
			if(hash == "0")
			{
				endPart = data->find("char peer1_", header);
				printf("----------\nClient\n----------\n");
				sk = data->find("{", header);
				bool exitS = false;
				string tmp, tmp2, tmp3, endStr="";
				unsigned long curLength = 0;

				while(header < endPart)
				{
					header = data->find("char peer0", header);
					sk = data->find("{", header);
					fk = data->find("}", header);
					tmp2 = data->substr(sk+1, fk-sk-2);
					tmp3 = data->substr(header, fk-header+2);
					curLength += tmp3.length()+4;
					//if(curLength+6 < endPart)
					//{
						tmp = tmp2;
						tmp.erase(0, 2);
						int count=2;
						exitS=false;
						while(!exitS)
						{
							tmp.erase(count, 4);
							count+=2;
							if(count+4 > tmp.length())
							{
								exitS = true;
							}
						}
						endStr += tmp;
						header = fk;
						header = data->find("char peer0", header);
					//}
				}
				header = endPart;
				tmp = endStr;
				unsigned char test[500000];
				memset(&test, 0, sizeof(test));
				int pointer= 0;
				for(int i = 0; i<tmp.length();i+=2)
				{
					if(i>0)
					{
						pointer = i/2;
					}
					unsigned char zw[2];
					zw[0] = GetDigitValue(tmp.c_str()[i]);
					zw[1] = GetDigitValue(tmp.c_str()[i+1]);
					test[pointer] = ((zw[0] * 16) + zw[1]) &0x000000ff;
				}
				test[pointer+1] = '\0';
				decompressClient(test, pointer);

			}
			else if(hash == "1")
			{
				endPart = data->find("char peer0", header);
				printf("----------\nServer\n----------\n");
				sk = data->find("{", header);
				bool exitS = false;
				string tmp, tmp2, tmp3, endStr="";
				unsigned long curLength = 0;

				while(header < endPart)
				{
					header = data->find("char peer1", header);
					sk = data->find("{", header);
					fk = data->find("}", header);
					tmp2 = data->substr(sk+1, fk-sk-2);
					tmp3 = data->substr(header, fk-header+2);
					curLength += tmp3.length()+4;
					//if(curLength+6 < endPart)
					//{
						tmp = tmp2;
						tmp.erase(0, 2);
						int count=2;
						exitS=false;
						while(!exitS)
						{
							tmp.erase(count, 4);
							count+=2;
							if(count+4 > tmp.length())
							{
								exitS = true;
							}
						}
						endStr += tmp;
						header = fk;
						header = data->find("char peer1", header);
					//}
				}
				header = endPart;
				tmp = endStr;
				unsigned char test[500000];
				int pointer= 0;
				for(int i = 0; i<tmp.length();i+=2)
				{
					if(i>0)
					{
						pointer = i/2;
					}
					unsigned char zw[2];
					zw[0] = GetDigitValue(tmp.c_str()[i]);
					zw[1] = GetDigitValue(tmp.c_str()[i+1]);
					test[pointer] = ((zw[0] * 16) + zw[1]) &0x000000ff;
				}
				test[pointer+1] = '\0';
				decompressServer(test, pointer);
			}
			else
			{
				printf("ERROR: %s\n", hash.c_str());
			}
		}
		else
		{
			exitProg = true;
		}

	}
}
void DoAll()
{
	string buf;
	string dest;
	ofstream spawn("spawns.txt",ios::out);
	ofstream server_output("All_out.txt",ios::out);
	ifstream file("All_in.txt",ios::in);
	unsigned int counter = 0 ;
	if(file)
	{
		cout<<"File opened(All). Now parsing strings\n";
		//convert to one
		while(!file.eof())
		{
			getline(file,buf);
			dest+=buf;
			buf.clear();
			++counter;

		}
		extract(&dest);
		server_output.close();

	}
	else
	{
		cout<<"Cannot open the file(All) "<<endl;
	}
}

void decompressClient(unsigned char data[500000], unsigned long length)
{
	ofstream server_output("All_out.txt",ios::app);
	//ZeroMemory(Cdest, 500000);
	memset(&Cdest, 0, sizeof(Cdest));
	printf("Size: %i\n", length);
	stream->next_out = (Bytef*)Cdest;
	stream->avail_out = sizeof(Cdest);
	stream->zalloc = 0;
	stream->zfree = 0;

	if(first_pack)
	{
		stream->next_in = (Bytef*)data+9;
		stream->avail_in = length+1-9;
		err = inflateInit(stream);
		first_pack=false;
	}
	else
	{
		stream->next_in = (Bytef*)data;
		stream->avail_in = length+1;
	}

	err = inflate(stream, Z_SYNC_FLUSH);
	if(err!=0)
	{
		printf("Error at decoding with error-code: %i\n", err);
	}
	unsigned long buf_len = (Cdest[0]&0x000000ff)*0x01000000+(Cdest[1]&0x000000ff)*0x00010000+(Cdest[2]&0x000000ff)*0x00000100+(Cdest[3]&0x000000ff)*0x00000001;
	unsigned long buf_len2=0;
	unsigned long startVal = 0;
	bool endPack = false;
	if(buf_len>0)
	{
		char x[8];
		while(!endPack)
		{
			unsigned long rec= 1000;
			unsigned long sen= 1000;
			clientCount++;
			server_output << "#################\nCLIENTPACKET "<< clientCount << "\n#################\n";
			for(int i=startVal;i<buf_len+4;i++)
			{
				sprintf(x, "%02x", (Cdest[i] & 0x000000ff));
				if(i > startVal)
				{
					server_output << " " << x[0] << x[1];
				}
				else
				{
					server_output << x[0] << x[1];
				}
				if((i-startVal) == 9)
				{
					rec = Cdest[i];
				}
				if(i-startVal == 9+rec+10)
				{
					sen = Cdest[i];
				}
				if(i-startVal == 9+rec+10+sen+8)
				{
					server_output << "\nOpcode: ";
				}
				if(i-startVal == 9+rec+10+sen+8+4)
				{
					server_output << "\n";
				}
			}

			server_output << "\n\n";

			try
			{
				buf_len2 = buf_len;
				buf_len = (Cdest[buf_len2+4]&0x000000ff)*0x01000000+(Cdest[buf_len2+5]&0x000000ff)*0x00010000+(Cdest[buf_len2+6]&0x000000ff)*0x00000100+(Cdest[buf_len2+7]&0x000000ff)*0x00000001;
				if(buf_len > 0)
				{
					buf_len += buf_len2+4;
					startVal = buf_len2+4;
				}
				else
				{
					endPack = true;
				}
			}
			catch(char* msg)
			{
				endPack = true;
				printf("Exception Error: %s\n", msg);
			}

		}
	}
	else
	{
		printf("ERROR - BufLength: 0x%08x\n", buf_len, data);
		server_output << Cdest << "\n";
		exitProg = true;
	}
}

void decompressServer(unsigned char data[500000], unsigned long length)
{
	ofstream server_output("All_out.txt",ios::app);
	ofstream spawn("spawns.txt",ios::app);
	ZeroMemory(Cdest2, 500000);
	if(first_pack2)
	{
		stream2->next_in = (Bytef*)data+9;
		stream2->avail_in = length+1-9;
		stream2->next_out = (Bytef*)Cdest2;
		stream2->avail_out = (uInt)500000;
		stream2->zalloc = 0;
		stream2->zfree = 0;
		err2 = inflateInit(stream2);
		if(err2 > 0)
		{
			printf("ERROR AT INIT stream2\n");
		}
		first_pack2=false;
	}
	else
	{
		stream2->next_in = (Bytef*)data;
		stream2->avail_in = length+1;
		stream2->next_out = (Bytef*)Cdest2;
		stream2->avail_out = (uInt)500000;
		stream2->zalloc = 0;
		stream2->zfree = 0;
	}
	try
	{
		err2 = inflate(stream2, Z_SYNC_FLUSH);
		
		unsigned long buf_len = (Cdest2[0]&0x000000ff)*0x01000000+(Cdest2[1]&0x000000ff)*0x00010000+(Cdest2[2]&0x000000ff)*0x00000100+(Cdest2[3]&0x000000ff)*0x00000001;
		unsigned long buf_len2=0;
		unsigned long startVal = 0;
		bool endPack = false;
		if(buf_len>0)
		{
			char x[8];
			while(!endPack)
			{
				unsigned long rec= 1000;
				unsigned long sen= 1000;
				unsigned long opcode = 0;
				unsigned long passblobOpcode = 0;
				unsigned long npcId=0;
				serverCount++;
				server_output << "-----------------\nSERVERPACKET " << serverCount << "\n-----------------\n";
				for(int i=startVal;i<buf_len+4;i++)
				{
					sprintf(x, "%02x", (Cdest2[i] & 0x000000ff));
					if(i > 0)
					{
						server_output << " " << x[0] << x[1];
					}
					else
					{
						server_output << " " << x[0] << x[1];
					}
					if((i-startVal) == 9)
					{
						rec = Cdest2[i];
					}
					if(i-startVal == 9+rec+10)
					{
						sen = Cdest2[i];
					}
					if(i-startVal == 9+rec+10+sen+8)
					{
						server_output << "\nOpcode: ";
						opcode = (Cdest2[i+1] & 0x000000ff) * 0x01000000 + (Cdest2[i+2] & 0x000000ff) * 0x00010000 + (Cdest2[i+3] & 0x000000ff) * 0x00000100 + (Cdest2[i+4] & 0x000000ff) * 0x00000001;
						//spawn << "Opcode: " << opcode << "\n";
					}
					if(i-startVal == 9+rec+10+sen+8+4)
					{
						server_output << "\n";
					}
					if(opcode == 0 && i-startVal == 9+rec+10+sen+8+4+1+4)
					{
						passblobOpcode = (Cdest2[i] & 0x000000ff) * 0x01000000 + (Cdest2[i+1] & 0x000000ff) * 0x00010000 + (Cdest2[i+2] & 0x000000ff) * 0x00000100 + (Cdest2[i+3] & 0x000000ff) * 0x00000001;
						//spawn << "PassBlobOpcode: " << passblobOpcode << "\n";
					}
					if(opcode == 0 && passblobOpcode == 0xfa015807 && i-startVal == rec+sen+39+4+1)
					{
						npcId = (Cdest2[i] & 0x000000ff) * 0x01000000 + (Cdest2[i+1] & 0x000000ff) * 0x00010000 + (Cdest2[i+2] & 0x000000ff) * 0x00000100 + (Cdest2[i+3] & 0x000000ff) * 0x00000001;
						if((npcId & 0xff000000) == 0)
						{
							spawn << "NPCId: " << npcId << "\n";
						}
						npcId = 0;
					}
					if((npcId & 0xff000000) == 0 && opcode == 0 && passblobOpcode == 0xfa015807 && i-startVal == rec+sen+43+0)
					{
						//spawn << "NPCId: " << npcId << "\n";
					}
				}

				server_output << "\n\n";

				try
				{
					buf_len2 = buf_len;
					buf_len = (Cdest2[buf_len2+4]&0x000000ff)*0x01000000+(Cdest2[buf_len2+5]&0x000000ff)*0x00010000+(Cdest2[buf_len2+6]&0x000000ff)*0x00000100+(Cdest2[buf_len2+7]&0x000000ff)*0x00000001;
					if(buf_len > 0)
					{
						buf_len += buf_len2+4;
						startVal = buf_len2+4;
					}
					else
					{
						endPack = true;
					}
				}
				catch(char* msg)
				{
					endPack = true;
					printf("Exception Error: %s\n", msg);
				}

			}
		}
		else
		{
			printf("ERROR - BufLength: 0x%08x\n", buf_len);
			exitProg = true;
		}
	}
	catch(char* msg)
	{
		printf("Exception Error\n");
	}
}

char GetDigitValue(char digit)
{
	int asciiOffset, digitValue;
	if (digit >= 48 && digit <= 57)
	{
		// code for '0' through '9'
		asciiOffset = 48;
		digitValue = digit - asciiOffset;
		return digitValue;
	}
	else if (digit >= 65 && digit <= 70)
	{
		// digit is 'A' through 'F'
		asciiOffset = 55;
		digitValue = digit - asciiOffset;
		return digitValue;
	}
	else if (digit >= 97 && digit <= 102)
	{
		// code for 'a' through 'f'
		asciiOffset = 87;
		digitValue = digit - asciiOffset;
		return digitValue;
	}
	else
	{
		// illegal digit
		return 0;
	}
}