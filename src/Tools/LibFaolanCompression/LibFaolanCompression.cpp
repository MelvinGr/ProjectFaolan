#pragma once

#define ZLIB_WINAPI
#include "zlib/zlib.h"

using namespace System;
using namespace System::IO;

typedef unsigned char uint8;
typedef unsigned int uint32;

#define LogErrorLine Console::Error->WriteLine
#define CHUNK 16384

namespace LibFaolan
{
	public ref class LibFaolanCompression
	{
		z_stream* inflateStream = NULL;
		//z_stream* deflateStream = NULL;

	public:
		~LibFaolanCompression()
		{
			if (inflateStream != NULL)
			{
				delete inflateStream;
				inflateStream = NULL;
			}

			/*if (deflateStream != NULL)
			{
				delete deflateStream;
				deflateStream = NULL;
			}*/
		}

		array<System::Byte>^ Inflate(array<System::Byte>^ input)
		{
			if (input == nullptr || input->Length == 0)
				return nullptr;

			MemoryStream^ memstream = gcnew MemoryStream();
			pin_ptr<System::Byte> pinnedInput = &input[0];

			///////////////////////////////////////

			if (inflateStream == NULL)
			{
				inflateStream = new z_stream();
				inflateStream->zalloc = NULL;
				inflateStream->zfree = NULL;
				inflateStream->next_in = pinnedInput + 9;
				inflateStream->avail_in = input->Length - 9;
				inflateInit(inflateStream);
			}
			else
			{
				inflateStream->next_in = pinnedInput;
				inflateStream->avail_in = input->Length;
			}

			uint8 outBuffer[CHUNK];

			do
			{
				inflateStream->next_out = outBuffer;
				inflateStream->avail_out = CHUNK;

				int error = inflate(inflateStream, Z_SYNC_FLUSH);
				if (error != Z_OK)
				{
					LogErrorLine("Error while inflating! (zlib Error: {0})", error);
					return nullptr;
				}

				uint32 have = CHUNK - inflateStream->avail_out;
				for (uint32 i = 0; i < have; i++)
					memstream->WriteByte(outBuffer[i]);
			} while (inflateStream->avail_in != 0);

			return memstream->ToArray();
		}

		/*array<System::Byte>^ Deflate(array<System::Byte>^ input)
		{
			if (input == nullptr || input->Length == 0)
				return nullptr;

			MemoryStream^ memstream = gcnew MemoryStream();
			pin_ptr<uint8> pinnedInput = &input[0];

			///////////////////////////////////////
			
			if (deflateStream == NULL)
			{
				deflateStream = new z_stream();
				inflateStream->zalloc = NULL;
				inflateStream->zfree = NULL;
				deflateInit(inflateStream, Z_DEFAULT_COMPRESSION);
			}

			deflateStream->avail_in = input->Length;
			deflateStream->next_in = pinnedInput;
			
			uint8 outBuffer[CHUNK];

			do
			{
				deflateStream->next_out = outBuffer;
				deflateStream->avail_out = CHUNK;

				int error = deflate(deflateStream, Z_SYNC_FLUSH);
				if (error != Z_OK)
				{
					LogErrorLine("Error while deflating! (zlib Error: {0})", error);
					return nullptr;
				}

				uint32 have = CHUNK - inflateStream->avail_out;
				for (uint32 i = 0; i < have; i++)
					memstream->WriteByte(outBuffer[i]);
			} while (deflateStream->avail_in != 0);
			
			return memstream->ToArray();
		}*/
	};
}