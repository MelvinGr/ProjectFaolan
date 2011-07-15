/*
This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program. If not, see <http://www.gnu.org/licenses/>.
*/

//World Server - 

enum StatID
{
	MAXHEALTH     = 0x00000001,
	MODHEALTH     = 0x0000001b,
	CURRENTPVPXP  = 0x00000033,
	CURRENTPVEXP  = 0x00000034,
	PLAYERLEVEL   = 0x00000036,
	EXP_OFFSET    = 0x00000039, // ?
	COINSLOWERPART = 0x0000003d, // Coins lower Part(CoinsValue & 0x00000000ffffffff)
	PLAYERPVPLEVEL = 0x00000046,
	SKILLPOINTS    = 0x000000f3,
	COINSHIGERPART = 0x0000014b,
	MAXPVEEXP	   = 0x0000015e,
	MAXPVPEXP	   = 0x00000161,
	MAXPVPEXP_OFFSET = 0x00000163,
	MODSTAMINA	   = 0x000001f8,
	MAXSTAMINA	   = 0x000001f9,
	MODMANA		   = 0x000001fa,
	MAXMANA		   = 0x000001fb,
	
	// PVP ATTRIBUTES
	
	PVPDEATH =			0x00000292,
	KILLBLOWSPVP =		0x00000290,
	ANNIHILATION_WIN =  0x0000029a,
	ANNIHILATION_LOSE = 0x0000029b,
	CTS_WIN		 = 0x0000029c, // Capture The Skull
	CTS_LOSE	 = 0x0000029d,
	SIEGE_WIN	 = 0x0000029e,
	SIEGE_LOSE	 = 0x0000029f,
	MURDERPOINTS = 0x00000449,
	
	// Player Attributes

	ATTRIB_STRENGTH		= 0x00000324,
	ATTRIB_CONSTITUTION = 0x00000326,
	ATTRIB_INTELLIGENCE = 0x00000328,
	ATTRIB_WISDOM		= 0x0000032a,
	ATTRIB_DEXTERITY	= 0x0000032e
};

enum Opcodes
{
	/*
	GI ... GameInterface (Server)
	GA ... GameAgent (Client)
	*/
	GI_PassBlob					= 0,
	GI_AckAuthentication		= 1,
	GI_RedirectToServer			= 2,
	GI_OnDisconnectGame			= 3,
	GI_OnQuitGame				= 4,
	GI_Pong						= 5,
	GI_PongEngine				= 6,
	GI_InfoToUser				= 7,
	GI_GameStats				= 8,
	GI_GameServerStats			= 9,
	GI_GameServerDataSet		= 10,
	GI_ReportGameServerDataset	= 13, //changed not coorect !!!
	GI_ReportDimesionID			= 11, //changed
	GI_ReportServerID			= 12, //changed
	GI_DataSink					= 14,
	GI_Kicked					= 15,

	GA_PassBlob					= 0,
	GA_Ping						= 1,
	GA_ChatVicinity				= 2,
	GA_Authenticate				= 3,
	GA_GetStats					= 4,
	GA_UpdateGameServerStats	= 5,
	GA_DataSink					= 6,
	GA_ClientDisconnected		= 7,
	GA_DelayedAuthenticate		= 8,


	/*
	GCI ... GameCharInterface (Server)
	GCA ... GameCharAgent (Client)
	*/
	GCI_UpdateAggroStatus		= 0,
	GCI_UpdateName				= 1,
	GCI_addItem					= 0x1c,
	GCI_Pong					= 0x77,

	GCA_Ping					= 0x7c
};

enum PassBlobOpcodes
{
	/*
	All known Passblob-Opcodes
	*/
	CHAR_MOVEMENT		= 0x4517aa81,
	CHAR_ACTION			= 0x642cd3d6,
	SELECT_OBJECT		= 0x0d155695,
	DELETE_ITEM			= 0x72491de9,
	LOADBAR_FULL		= 0x864cfef8,
	UPDATE_STATS		= 0x96b8dc59,
	LOGOUT				= 0x6b6fd368
};