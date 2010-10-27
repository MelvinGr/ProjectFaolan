/*
MySQL Data Transfer
Source Host: localhost
Source Database: test
Target Host: localhost
Target Database: test
Date: 2010-01-22 00:15:18
*/

SET FOREIGN_KEY_CHECKS=0;
-- ----------------------------
-- Table structure for accounts
-- ----------------------------
CREATE TABLE `accounts` (
  `account_id` int(10) unsigned NOT NULL,
  `username` varchar(50) COLLATE latin1_general_ci NOT NULL,
  `password` varchar(32) COLLATE latin1_general_ci NOT NULL,
  `mail` varchar(100) COLLATE latin1_general_ci DEFAULT NULL,
  `kind` tinyint(3) unsigned NOT NULL,
  `cookie` int(10) unsigned DEFAULT NULL,
  `state` int(11) DEFAULT NULL,
  `last_connection` varchar(255) COLLATE latin1_general_ci DEFAULT NULL,
  `last_ip` varchar(15) COLLATE latin1_general_ci DEFAULT NULL,
  `date_creation` varchar(255) COLLATE latin1_general_ci DEFAULT NULL,
  `date_modif` varchar(255) COLLATE latin1_general_ci DEFAULT NULL,
  PRIMARY KEY (`account_id`)
) ENGINE=MyISAM DEFAULT CHARSET=latin1 COLLATE=latin1_general_ci;

-- ----------------------------
-- Table structure for archetypes
-- ----------------------------
CREATE TABLE `archetypes` (
  `arche_id` tinyint(3) unsigned NOT NULL,
  `arche_name` varchar(50) COLLATE latin1_general_ci NOT NULL,
  `arche_desc` text COLLATE latin1_general_ci,
  PRIMARY KEY (`arche_id`)
) ENGINE=MyISAM DEFAULT CHARSET=latin1 COLLATE=latin1_general_ci;

-- ----------------------------
-- Table structure for body
-- ----------------------------
CREATE TABLE `body` (
  `character_id` smallint(5) NOT NULL,
  `arm` double(3,2) NOT NULL,
  `chest` double(3,2) NOT NULL,
  `bosom` double(3,2) NOT NULL,
  `stomach` double(3,2) NOT NULL,
  `arse` double(3,2) NOT NULL,
  `thigh` double(3,2) NOT NULL,
  `leg` double(3,2) NOT NULL,
  PRIMARY KEY (`character_id`)
) ENGINE=MyISAM DEFAULT CHARSET=latin1 COLLATE=latin1_general_ci;

-- ----------------------------
-- Table structure for characters
-- ----------------------------
CREATE TABLE `characters` (
  `character_id` int(11) NOT NULL,
  `account_id` int(11) NOT NULL,
  `name` char(255) COLLATE latin1_general_ci NOT NULL,
  `race` int(11) NOT NULL,
  `class` int(11) NOT NULL,
  `level` int(11) NOT NULL,
  `sex` int(11) NOT NULL,
  `realm_id` int(11) NOT NULL,
  `map_id` int(11) NOT NULL,
  `language` char(255) COLLATE latin1_general_ci NOT NULL,
  `headmesh` int(11) NOT NULL,
  `size` int(11) NOT NULL,
  `voice` int(11) NOT NULL,
  `last_connection` char(255) COLLATE latin1_general_ci NOT NULL,
  `pos_x` int(11) NOT NULL,
  `pos_y` int(11) NOT NULL,
  `pos_z` int(11) NOT NULL,
  `lbinprv` int(11) NOT NULL,
  PRIMARY KEY (`character_id`)
) ENGINE=MyISAM DEFAULT CHARSET=latin1 COLLATE=latin1_general_ci;

-- ----------------------------
-- Table structure for classes
-- ----------------------------
CREATE TABLE `classes` (
  `class_id` tinyint(3) unsigned NOT NULL,
  `class_name` varchar(50) COLLATE latin1_general_ci NOT NULL,
  `class_desc` text COLLATE latin1_general_ci,
  `class_client_id` tinyint(3) NOT NULL,
  PRIMARY KEY (`class_id`)
) ENGINE=MyISAM DEFAULT CHARSET=latin1 COLLATE=latin1_general_ci;

-- ----------------------------
-- Table structure for face
-- ----------------------------
CREATE TABLE `face` (
  `character_id` tinyint(5) NOT NULL,
  `EyebrowScale` double(3,2) NOT NULL,
  `CheekDepth` double(3,2) NOT NULL,
  `CheekHeight` double(3,2) NOT NULL,
  `CheekWidth` double(3,2) NOT NULL,
  `ChinLength` double(3,2) NOT NULL,
  `ChinWidth` double(3,2) NOT NULL,
  `EarAngle` double(3,2) NOT NULL,
  `Ears` double(3,2) NOT NULL,
  `EyesAngle` double(3,2) NOT NULL,
  `EyesVerticalPos` double(3,2) NOT NULL,
  `EyesHorizontalPos` double(3,2) NOT NULL,
  `EyesDepth` double(3,2) NOT NULL,
  `JawWidth` double(3,2) NOT NULL,
  `FaceLength` double(3,2) NOT NULL,
  `MouthVerticalPos` double(3,2) NOT NULL,
  `MouthWidth` double(3,2) NOT NULL,
  `NoseAngle` double(3,2) NOT NULL,
  `NoseCurve` double(3,2) NOT NULL,
  `Crooked_Nose` double(3,2) NOT NULL,
  `NoseLength` double(3,2) NOT NULL,
  `NoseWidth` double(3,2) NOT NULL,
  PRIMARY KEY (`character_id`)
) ENGINE=MyISAM DEFAULT CHARSET=latin1 COLLATE=latin1_general_ci;

-- ----------------------------
-- Table structure for maps
-- ----------------------------
CREATE TABLE IF NOT EXISTS `maps` (
  `map_id` int(11) NOT NULL,
  `map_name` varchar(150) collate latin1_general_ci default NULL,
  `position_0` int(11) NOT NULL,
  `position_1` int(11) NOT NULL,
  `position_2` int(11) NOT NULL,
  `rotation_0` int(11) NOT NULL,
  `rotation_1` int(11) NOT NULL,
  `rotation_2` int(11) NOT NULL,
  PRIMARY KEY  (`map_id`)
) ENGINE=MyISAM DEFAULT CHARSET=latin1 COLLATE=latin1_general_ci;

-- ----------------------------
-- Table structure for npcs
-- ----------------------------
CREATE TABLE IF NOT EXISTS `npcs` (
  `id` int(11) NOT NULL,
  `name` varchar(255) NOT NULL,
  `nameId` bigint(12) NOT NULL default '0',
  `map` int(11) NOT NULL,
  `action` int(11) NOT NULL,
  `level` int(11) NOT NULL,
  `fraction` int(11) NOT NULL default '0',
  `health` int(11) NOT NULL default '0',
  `stamina` int(11) NOT NULL default '0',
  `mana` int(11) NOT NULL default '0',
  `getExp` int(11) NOT NULL default '0',
  `extras` longtext NOT NULL,
  PRIMARY KEY  (`id`)
) ENGINE=MyISAM DEFAULT CHARSET=latin1;

-- ----------------------------
-- Table structure for npc_details
-- ----------------------------
CREATE TABLE IF NOT EXISTS `npc_details` (
  `id` int(11) NOT NULL default '0',
  `position_x` bigint(12) NOT NULL default '0',
  `position_y` bigint(12) NOT NULL default '0',
  `position_z` bigint(12) NOT NULL default '0',
  `rotation_x` bigint(12) NOT NULL default '0',
  `rotation_y` bigint(12) NOT NULL default '0',
  `rotation_z` bigint(12) NOT NULL default '0',
  `bodymesh` int(11) NOT NULL default '0',
  `headmesh` int(11) NOT NULL default '0',
  `hairmesh` int(11) NOT NULL default '0',
  `beardmesh` int(11) NOT NULL default '0',
  `npcsize` int(11) NOT NULL default '0',
  PRIMARY KEY  (`id`)
) ENGINE=MyISAM DEFAULT CHARSET=latin1;

-- ----------------------------
-- Table structure for spelldata
-- ----------------------------
DROP TABLE IF EXISTS `spelldata`;
CREATE TABLE IF NOT EXISTS `spelldata` (
  `id` bigint(255) NOT NULL DEFAULT '0',
  `level` int(255) NOT NULL DEFAULT '0',
  `name` varchar(255) NOT NULL DEFAULT '',
  `castTime` int(11) NOT NULL DEFAULT '0',
  `minDmg` int(11) NOT NULL DEFAULT '0',
  `maxDmg` int(11) NOT NULL DEFAULT '0',
  `costMana` int(11) NOT NULL DEFAULT '0',
  `effectSpell` varchar(255) NOT NULL DEFAULT ''
) ENGINE=MyISAM DEFAULT CHARSET=latin1;

-- ----------------------------
-- Table structure for npc_items
-- ----------------------------
CREATE TABLE IF NOT EXISTS `npc_items` (
  `id` int(11) NOT NULL default '0' COMMENT 'ID of npc',
  `neck` bigint(12) NOT NULL default '0' COMMENT '0x01',
  `head` bigint(12) NOT NULL default '0' COMMENT '0x02',
  `tokens` bigint(12) NOT NULL default '0' COMMENT '0x03',
  `chest` bigint(12) NOT NULL default '0' COMMENT '0x05',
  `main_hand_left` bigint(12) NOT NULL default '0' COMMENT '0x06',
  `belt` bigint(12) NOT NULL default '0' COMMENT '0x07',
  `off_hand_left` bigint(12) NOT NULL default '0' COMMENT '0x08',
  `ring_left` bigint(12) NOT NULL default '0' COMMENT '0x09',
  `ring_right` bigint(12) NOT NULL default '0' COMMENT '0x0a',
  `cloak` bigint(12) NOT NULL default '0' COMMENT '0x0b',
  `shoulder` bigint(12) NOT NULL default '0' COMMENT '0x0c',
  `boots` bigint(12) NOT NULL default '0' COMMENT '0x0e',
  `hands` bigint(12) NOT NULL default '0' COMMENT '0x0f',
  `legs` bigint(12) NOT NULL default '0' COMMENT '0x10',
  `first_pos_bag` bigint(12) NOT NULL default '0' COMMENT '0x11',
  `wrist` bigint(12) NOT NULL default '0' COMMENT '0x12',
  `main_hand_right` bigint(12) NOT NULL default '0' COMMENT '0x14',
  `off_hand_right` bigint(12) NOT NULL default '0' COMMENT '0x15',
  PRIMARY KEY  (`id`)
) ENGINE=MyISAM DEFAULT CHARSET=latin1;

-- ----------------------------
-- Table structure for race_arche_class
-- ----------------------------
CREATE TABLE `race_arche_class` (
  `race_id` tinyint(3) unsigned NOT NULL,
  `arche_id` tinyint(3) unsigned NOT NULL,
  `class_id` tinyint(3) unsigned NOT NULL,
  PRIMARY KEY (`race_id`,`arche_id`,`class_id`),
  KEY `fk_RAC_arche` (`arche_id`),
  KEY `fk_RAC_class` (`class_id`)
) ENGINE=MyISAM DEFAULT CHARSET=latin1 COLLATE=latin1_general_ci;

-- ----------------------------
-- Table structure for races
-- ----------------------------
CREATE TABLE `races` (
  `race_id` tinyint(3) unsigned NOT NULL,
  `race_name` varchar(25) COLLATE latin1_general_ci NOT NULL,
  `race_desc` text COLLATE latin1_general_ci,
  PRIMARY KEY (`race_id`)
) ENGINE=MyISAM DEFAULT CHARSET=latin1 COLLATE=latin1_general_ci;

-- ----------------------------
-- Table structure for const_abilitys
-- ----------------------------
DROP TABLE IF EXISTS `const_abilitys`;
CREATE TABLE IF NOT EXISTS `const_abilitys` (
  `class` int(12) NOT NULL DEFAULT '0',
  `level` int(12) NOT NULL DEFAULT '0',
  `abilitys` longtext,
  `abilitysNew` longtext,
  `spells` longtext,
  `spellsNew` longtext
) ENGINE=MyISAM DEFAULT CHARSET=latin1;

-- ----------------------------
-- Table structure for const_stats
-- ----------------------------
DROP TABLE IF EXISTS `const_stats`;
CREATE TABLE IF NOT EXISTS `const_stats` (
  `level` int(11) NOT NULL,
  `race` int(11) NOT NULL,
  `class` int(11) NOT NULL,
  `health` int(11) NOT NULL,
  `mana` int(11) NOT NULL,
  `stamina` int(11) NOT NULL,
  `exp` int(11) NOT NULL DEFAULT '100',
  `S` int(11) NOT NULL,
  `I` int(11) NOT NULL,
  `C` int(11) NOT NULL,
  `D` int(11) NOT NULL,
  `W` int(11) NOT NULL,
  `expOffset` int(11) NOT NULL DEFAULT '0'
) ENGINE=MyISAM DEFAULT CHARSET=latin1;

-- ----------------------------
-- Table structure for p_abilities
-- ----------------------------
DROP TABLE IF EXISTS `p_abilities`;
CREATE TABLE IF NOT EXISTS `p_abilities` (
  `name` text NOT NULL,
  `id` int(11) NOT NULL DEFAULT '0',
  `slot` int(11) NOT NULL DEFAULT '0',
  `data0` bigint(12) NOT NULL DEFAULT '0',
  `data1` bigint(12) NOT NULL DEFAULT '0',
  `data2` bigint(12) NOT NULL DEFAULT '0',
  `data3` bigint(12) NOT NULL DEFAULT '0',
  `data4` bigint(12) NOT NULL DEFAULT '0',
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=latin1;

-- ----------------------------
-- Table structure for p_abilities
-- ----------------------------
DROP TABLE IF EXISTS `player_items`;
CREATE TABLE IF NOT EXISTS `player_items` (
  `id` int(11) NOT NULL DEFAULT '0' COMMENT 'ID from player',
  `neck` bigint(12) NOT NULL DEFAULT '0' COMMENT '0x01',
  `head` bigint(12) NOT NULL DEFAULT '0' COMMENT '0x02',
  `tokens` bigint(12) NOT NULL DEFAULT '0' COMMENT '0x03',
  `chest` bigint(12) NOT NULL DEFAULT '0' COMMENT '0x05',
  `main_hand_left` bigint(12) NOT NULL DEFAULT '0' COMMENT '0x06',
  `belt` bigint(12) NOT NULL DEFAULT '0' COMMENT '0x07',
  `off_hand_left` bigint(12) NOT NULL DEFAULT '0' COMMENT '0x08',
  `ring_left` bigint(12) NOT NULL DEFAULT '0' COMMENT '0x09',
  `ring_right` bigint(12) NOT NULL DEFAULT '0' COMMENT '0x0a',
  `cloak` bigint(12) NOT NULL DEFAULT '0' COMMENT '0x0b',
  `shoulder` bigint(12) NOT NULL DEFAULT '0' COMMENT '0x0c',
  `boots` bigint(12) NOT NULL DEFAULT '0' COMMENT '0x0e',
  `hands` bigint(12) NOT NULL DEFAULT '0' COMMENT '0x0f',
  `legs` bigint(12) NOT NULL DEFAULT '0' COMMENT '0x10',
  `first_pos_bag` bigint(12) NOT NULL DEFAULT '0' COMMENT '0x11',
  `wrist` bigint(12) NOT NULL DEFAULT '0' COMMENT '0x12',
  `main_hand_right` bigint(12) NOT NULL DEFAULT '0' COMMENT '0x14',
  `off_hand_right` bigint(12) NOT NULL DEFAULT '0' COMMENT '0x15',
  `bag` longtext,
  `quest` longtext,
  `ressources` longtext,
  PRIMARY KEY (`id`)
) ENGINE=MyISAM DEFAULT CHARSET=latin1;

-- ----------------------------
-- Table structure for playerstats
-- ----------------------------
DROP TABLE IF EXISTS `playerstats`;
CREATE TABLE IF NOT EXISTS `playerstats` (
  `id` int(11) NOT NULL DEFAULT '0' COMMENT 'charId',
  `health` int(11) NOT NULL DEFAULT '0' COMMENT 'current health',
  `mana` int(11) NOT NULL DEFAULT '0' COMMENT 'current mana',
  `stamina` int(11) NOT NULL DEFAULT '0' COMMENT 'current stamina',
  `exp` int(11) NOT NULL DEFAULT '0' COMMENT 'current exp',
  `pvpexp` int(11) NOT NULL DEFAULT '0' COMMENT 'current pvp exp',
  `siegeLose` int(11) NOT NULL DEFAULT '0' COMMENT 'siege lose',
  `siegeWin` int(11) NOT NULL DEFAULT '0' COMMENT 'siege win',
  `ctsLose` int(11) NOT NULL DEFAULT '0' COMMENT 'Capture the Skull Lose',
  `ctsWin` int(11) NOT NULL DEFAULT '0' COMMENT 'Capture the Skull Win',
  `annihilationLose` int(11) NOT NULL DEFAULT '0' COMMENT 'annihilation Lose',
  `annihilationWin` int(11) NOT NULL DEFAULT '0' COMMENT 'annihilation Win',
  `death` int(11) NOT NULL DEFAULT '0' COMMENT 'Death PvP Stat',
  `killingBlow` int(11) NOT NULL DEFAULT '0' COMMENT 'Killing Blow',
  `murderPoints` int(11) NOT NULL DEFAULT '0' COMMENT 'Murder Points',
  `coins` int(11) NOT NULL DEFAULT '0' COMMENT 'Coins'
) ENGINE=MyISAM DEFAULT CHARSET=latin1;

-- ----------------------------
-- Records 
-- ----------------------------
INSERT INTO `accounts` VALUES ('1', 'root', 'toor', null, '0', '404', '0', '19/12/2009', '127.0.0.1', '10/09/2009', '10/09/2009');
INSERT INTO `archetypes` VALUES ('1', 'Priest', null);
INSERT INTO `archetypes` VALUES ('2', 'Soldier', null);
INSERT INTO `archetypes` VALUES ('3', 'Rogue', null);
INSERT INTO `archetypes` VALUES ('4', 'Mage', null);
INSERT INTO `classes` VALUES ('1', 'Priest of Mitra', null, '24');
INSERT INTO `classes` VALUES ('2', 'Tempest of Set', null, '28');
INSERT INTO `classes` VALUES ('3', 'Bear Shaman', null, '29');
INSERT INTO `classes` VALUES ('4', 'Conqueror', null, '22');
INSERT INTO `classes` VALUES ('5', 'Dark Templar', null, '31');
INSERT INTO `classes` VALUES ('6', 'Guardian', null, '20');
INSERT INTO `classes` VALUES ('7', 'Barbarian', null, '18');
INSERT INTO `classes` VALUES ('8', 'Ranger', null, '39');
INSERT INTO `classes` VALUES ('9', 'Assassin', null, '34');
INSERT INTO `classes` VALUES ('10', 'Herald of Xotli', null, '43');
INSERT INTO `classes` VALUES ('11', 'Demonologist', null, '44');
INSERT INTO `classes` VALUES ('12', 'Necromancer', null, '41');
INSERT INTO `maps` VALUES('4010', 'Tortage Beach', '1134641152', '1125207987', '1144011162', '0', '0', '0');
INSERT INTO `maps` VALUES('4025', 'Acheronian Ruins', '1114187917', '1124053233', '1134462075', '1056274244', '1063299392', '1793');
INSERT INTO `npcs` VALUES('433435', 'Scavenger Lackey', '12141', '4010', '3', '1', '1', '59', '59', '59', '50', '');
INSERT INTO `npcs` VALUES('433433', 'Scavenger Lackey', '12141', '4010', '3', '1', '1', '59', '59', '59', '50', '');
INSERT INTO `npcs` VALUES('433376', 'Kalanthes', '2704', '4010', '1', '80', '0', '3855', '3855', '3855', '0', '');
INSERT INTO `npcs` VALUES('433474', 'Casilda', '9411', '4010', '1', '1', '0', '59', '59', '59', '0', '1::GameAgent<->GameInterface<->0<->74<->642cd3d60000c35000069d4200040000000500026ec4000000000000000200000018000011b800000000000000000000000000000000000000000000000000000000000000003e4f4f3c');
INSERT INTO `npc_details` VALUES('433435', '1134607368', '1125238684', '1143222632', '1054102189', '1063842617', '1536', '140327', '191546', '13', '0', '100');
INSERT INTO `npc_details` VALUES('433433', '1134021280', '1125247291', '1143100195', '1054102189', '1063842617', '2816', '701595', '222567', '149', '0', '100');
INSERT INTO `npc_details` VALUES('433376', '1134991770', '1125272562', '1144029610', '1059471930', '3208817143', '11776', '137506', '160655', '0', '0', '100');
INSERT INTO `npc_details` VALUES('433474', '1135923790', '1125734178', '1144220353', '1063106982', '3204443122', '85760', '701595', '222567', '0', '0', '100');
INSERT INTO `npc_items` VALUES('433433', '0', '0', '0', '189905', '195325', '194918', '195325', '0', '0', '0', '0', '189896', '0', '193680', '0', '116743', '0', '0');
INSERT INTO `npc_items` VALUES('433435', '0', '0', '0', '189905', '195325', '194918', '195325', '0', '0', '0', '0', '0', '0', '193680', '0', '0', '0', '0');
INSERT INTO `npc_items` VALUES('433376', '0', '0', '0', '189905', '195325', '0', '0', '0', '0', '0', '0', '0', '0', '193680', '0', '0', '0', '0');
INSERT INTO `npc_items` VALUES('433474', '0', '0', '0', '189905', '195325', '194918', '195325', '0', '0', '0', '0', '189896', '0', '193680', '0', '116743', '0', '0');
INSERT INTO `race_arche_class` VALUES ('1', '1', '1');
INSERT INTO `race_arche_class` VALUES ('1', '2', '4');
INSERT INTO `race_arche_class` VALUES ('1', '2', '5');
INSERT INTO `race_arche_class` VALUES ('1', '2', '6');
INSERT INTO `race_arche_class` VALUES ('1', '3', '7');
INSERT INTO `race_arche_class` VALUES ('1', '3', '8');
INSERT INTO `race_arche_class` VALUES ('1', '3', '9');
INSERT INTO `race_arche_class` VALUES ('2', '1', '3');
INSERT INTO `race_arche_class` VALUES ('2', '2', '4');
INSERT INTO `race_arche_class` VALUES ('2', '2', '5');
INSERT INTO `race_arche_class` VALUES ('2', '2', '6');
INSERT INTO `race_arche_class` VALUES ('2', '3', '7');
INSERT INTO `race_arche_class` VALUES ('2', '3', '8');
INSERT INTO `race_arche_class` VALUES ('3', '1', '2');
INSERT INTO `race_arche_class` VALUES ('3', '3', '8');
INSERT INTO `race_arche_class` VALUES ('3', '3', '9');
INSERT INTO `race_arche_class` VALUES ('3', '4', '10');
INSERT INTO `race_arche_class` VALUES ('3', '4', '11');
INSERT INTO `race_arche_class` VALUES ('3', '4', '12');
INSERT INTO `races` VALUES ('1', 'Aquilonian', null);
INSERT INTO `races` VALUES ('2', 'Cimmerian', null);
INSERT INTO `races` VALUES ('3', 'Stygian', null);
INSERT INTO `characters` VALUES ('2018', '1', 'Conanhimself', '1', '18', '1', '2', '1', '4010', 'enEN', '793613', '95', '2', '00-00-0000 00:00', '1134641152', '1125207987', '1144011162', '112233');
INSERT INTO `maps` VALUES ('500', 'GM Island', '1133136232', '1102708380', '1135420002', '0', '0', '0');
INSERT INTO `maps` VALUES ('2070', 'Poitain', '1151341050', '1127124818', '1152928673', '0', '0', '0');
INSERT INTO `maps` VALUES ('1000', 'Conarch Village', '1114641152', '1114053233', '1144011162', '0', '0', '0');
INSERT INTO `maps` VALUES ('2000', 'Old Tarantia', '1146428220', '1120564870', '1140034652', '0', '0', '0');
INSERT INTO `maps` VALUES ('3020', 'Khopshef Province', '1141409216', '1094408432', '1149125668', '0', '0', '0');
INSERT INTO `p_abilities` (`name`, `id`, `slot`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES
('Rest', 269272, 106, 1312109365, 2023574725, 3133773807, 3870783406, 1887832719),
('Normal Attack Left', 769094, 108, 1263352118, 3236668225, 2153842419, 314062142, 3015939650),
('Normal Attack right', 769095, 109, 1346650673, 1688691897, 2528496312, 3832970358, 2802705639),
('Normal Attack Front', 769099, 112, 1227967307, 3443694845, 1660108369, 1296666148, 2957995211),
('toggle combat', 3316611, 1250, 1280331096, 2235756528, 749634890, 3339552459, 3958402763),
('switch weapon', 3675584, 259, 1278563891, 1677823321, 1538326685, 1383384407, 1833387476);
INSERT INTO `const_abilitys` VALUES (29, 1, '769094_1-769099_1', NULL, '165241', NULL);
INSERT INTO `const_abilitys` VALUES (44, 1, '769094_1-769099_1', NULL, NULL, NULL);
INSERT INTO `const_abilitys` VALUES (18, 1, '769094_1-769099_1', NULL, NULL, NULL);
INSERT INTO `spelldata` (`id`, `level`, `name`, `castTime`, `minDmg`, `maxDmg`, `costMana`, `effectSpell`) VALUES
(1148653, 1, 'Fire of Gehenna', 2000, 1500, 1700, 16, '1148647'),
(1148654, 1, 'Hellfire Stream', 1000, 800, 900, 16, ''),
(1148655, 1, 'Chained Shockstrike', 0, 1100, 1200, 0, '1148655'),
(1148656, 1, 'ShockStrike', 2500, 6000, 7300, 16, '1148655'),
(1148665, 1, 'Shield of Infernal Flames', 1500, 0, 0, 4, ''),
(1148666, 1, 'Storm Chains', 500, 100, 100, 29, ''),
(1148668, 1, 'Inferno of Amher', 2500, 9600, 11100, 29, ''),
(1148671, 1, 'Shocblast', 3000, 6100, 7400, 29, ''),
(1148672, 1, 'Protection of Set', 0, 0, 0, 0, ''),
(1148673, 1, 'Protection of Set', 1500, 0, 0, 4, ''),
(1148651, 1, 'Incerate (5)', 0, 100, 100, 0, '1148651'),
(1148650, 1, 'Incerate (4)', 0, 100, 100, 0, '1148651'),
(1148649, 1, 'Incerate (3)', 0, 100, 100, 0, '1148650'),
(1148648, 1, 'Incerate (2)', 0, 100, 100, 0, '1148649'),
(1148647, 1, 'Incerate (1)', 0, 0, 0, 0, '1148648'),
(1148644, 1, 'Black Ashes', 0, 0, 0, 0, ''),
(1148643, 1, 'Black Ashes', 0, 0, 0, 0, ''),
(1148640, 1, 'Flames of Hell', 0, 4700, 7700, 0, ''),
(1148639, 1, 'Gate of Hell', 0, 0, 0, 0, ''),
(1148638, 1, 'Gate of Hell', 0, 0, 0, 0, ''),
(1148590, 1, 'Gate of Hell', 1500, 0, 0, 19, '');
INSERT INTO `const_stats` (`level`, `race`, `class`, `health`, `mana`, `stamina`, `exp`, `S`, `I`, `C`, `D`, `W`, `expOffset`) VALUES
(1, 1, 29, 118, 171, 176, 100, 1, 1, 1, 1, 1, 0),
(1, 2, 28, 118, 0, 176, 100, 1, 1, 1, 1, 1, 0),
(1, 2, 24, 118, 0, 176, 100, 1, 1, 1, 1, 1, 0),
(1, 2, 22, 118, 0, 176, 100, 1, 1, 1, 1, 1, 0),
(1, 1, 20, 130, 0, 179, 100, 1, 1, 1, 1, 1, 0),
(1, 1, 18, 118, 0, 176, 100, 1, 1, 1, 1, 1, 0),
(1, 1, 31, 118, 0, 176, 100, 1, 1, 1, 1, 1, 0),
(1, 1, 34, 118, 0, 176, 100, 1, 1, 1, 1, 1, 0),
(1, 1, 39, 133, 0, 213, 100, 1, 1, 1, 1, 1, 0),
(1, 1, 41, 118, 0, 176, 100, 1, 1, 1, 1, 1, 0),
(1, 1, 43, 118, 0, 176, 100, 1, 1, 1, 1, 1, 0),
(1, 1, 44, 118, 0, 176, 100, 1, 1, 1, 1, 1, 0),
(1, 2, 18, 118, 0, 176, 100, 1, 1, 1, 1, 1, 0),
(1, 2, 20, 118, 0, 176, 100, 1, 1, 1, 1, 1, 0),
(1, 1, 22, 118, 0, 176, 100, 1, 1, 1, 1, 1, 0),
(1, 1, 24, 118, 0, 176, 100, 1, 1, 1, 1, 1, 0),
(1, 1, 28, 118, 0, 176, 100, 1, 1, 1, 1, 1, 0),
(1, 2, 29, 108, 171, 178, 100, 1, 1, 1, 1, 1, 0),
(1, 2, 31, 118, 0, 176, 100, 1, 1, 1, 1, 1, 0),
(1, 2, 34, 118, 0, 176, 100, 1, 1, 1, 1, 1, 0),
(1, 2, 39, 133, 0, 213, 100, 1, 1, 1, 1, 1, 0),
(1, 2, 41, 118, 0, 176, 100, 1, 1, 1, 1, 1, 0),
(1, 2, 43, 118, 0, 176, 100, 1, 1, 1, 1, 1, 0),
(1, 2, 44, 118, 0, 176, 100, 1, 1, 1, 1, 1, 0),
(1, 3, 18, 118, 0, 176, 100, 1, 1, 1, 1, 1, 0),
(1, 3, 20, 118, 0, 176, 100, 1, 1, 1, 1, 1, 0),
(1, 3, 22, 118, 0, 176, 100, 1, 1, 1, 1, 1, 0),
(1, 3, 24, 118, 0, 176, 100, 1, 1, 1, 1, 1, 0),
(1, 3, 28, 118, 0, 176, 100, 1, 1, 1, 1, 1, 0),
(1, 3, 29, 118, 0, 176, 100, 1, 1, 1, 1, 1, 0),
(1, 3, 31, 118, 0, 176, 100, 1, 1, 1, 1, 1, 0),
(1, 3, 34, 118, 0, 176, 100, 1, 1, 1, 1, 1, 0),
(1, 3, 39, 118, 0, 176, 100, 1, 1, 1, 1, 1, 0),
(1, 3, 41, 89, 246, 106, 100, 1, 1, 1, 1, 1, 0),
(1, 3, 43, 118, 0, 176, 100, 1, 1, 1, 1, 1, 0),
(1, 3, 44, 118, 200, 176, 100, 1, 1, 1, 1, 1, 0),
(2, 1, 18, 163, 0, 218, 340, 1, 1, 1, 1, 1, 100),
(3, 1, 18, 216, 0, 262, 400, 1, 1, 1, 1, 1, 0),
(2, 1, 29, 136, 209, 203, 340, 1, 1, 1, 1, 1, 100),
(2, 1, 20, 195, 0, 218, 340, 1, 1, 1, 1, 1, 100),
(2, 2, 39, 174, 0, 253, 340, 3, 2, 2, 4, 2, 100),
(3, 2, 39, 222, 0, 291, 500, 4, 2, 4, 6, 3, 0),
(4, 2, 39, 261, 0, 331, 1000, 6, 3, 5, 9, 4, 0),
(5, 2, 39, 309, 0, 371, 1500, 7, 4, 7, 12, 6, 0),
(2, 2, 29, 136, 209, 203, 340, 3, 3, 2, 2, 3, 100),
(3, 2, 29, 172, 247, 234, 300, 6, 4, 4, 4, 6, 0),
(4, 2, 29, 200, 285, 259, 1000, 8, 6, 5, 5, 8, 0),
(5, 2, 29, 237, 323, 290, 1500, 11, 7, 7, 7, 11, 0),
(2, 3, 41, 127, 288, 122, 340, 2, 4, 2, 2, 2, 100),
(3, 3, 41, 157, 333, 138, 500, 3, 7, 2, 4, 4, 0),
(4, 3, 41, 194, 375, 154, 1000, 4, 10, 3, 5, 5, 0),
(5, 3, 41, 232, 420, 170, 1500, 4, 13, 3, 7, 7, 0),
(6, 3, 41, 270, 459, 186, 2000, 5, 15, 5, 8, 8, 0),
(7, 3, 41, 300, 501, 200, 5000, 6, 18, 5, 9, 9, 0),
(8, 3, 41, 346, 546, 220, 10000, 7, 21, 7, 11, 11, 0),
(9, 3, 41, 376, 588, 234, 15000, 8, 24, 7, 12, 12, 0),
(10, 3, 41, 416, 630, 250, 20000, 9, 27, 8, 13, 13, 0),
(11, 3, 41, 447, 675, 266, 30000, 10, 30, 8, 15, 15, 0),
(12, 3, 41, 489, 717, 290, 50000, 11, 33, 9, 16, 16, 0),
(13, 3, 41, 557, 762, 306, 75000, 11, 36, 14, 18, 18, 0),
(14, 3, 41, 596, 804, 322, 100000, 12, 39, 15, 19, 19, 0),
(15, 3, 41, 627, 846, 336, 200000, 13, 42, 15, 20, 20, 0);