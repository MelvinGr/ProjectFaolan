-- phpMyAdmin SQL Dump
-- version 3.3.3
-- http://www.phpmyadmin.net
--
-- Host: localhost
-- Erstellungszeit: 05. Mai 2012 um 15:15
-- Server Version: 5.1.41
-- PHP-Version: 5.3.1

SET SQL_MODE="NO_AUTO_VALUE_ON_ZERO";

--
-- Datenbank: `faolan`
--

-- --------------------------------------------------------

--
-- Tabellenstruktur für Tabelle `accounts`
--

CREATE TABLE IF NOT EXISTS `accounts` (
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
  `trial` int(11) NOT NULL DEFAULT '1',
  PRIMARY KEY (`account_id`)
) ENGINE=MyISAM DEFAULT CHARSET=latin1 COLLATE=latin1_general_ci;

--
-- Daten für Tabelle `accounts`
--

INSERT INTO `accounts` (`account_id`, `username`, `password`, `mail`, `kind`, `cookie`, `state`, `last_connection`, `last_ip`, `date_creation`, `date_modif`, `trial`) VALUES(1, 'root', 'toor', NULL, 0, 2022, 0, '25/03/2012 18:32', '127.0.0.1', '10/09/2009', '10/09/2009', 0);

-- --------------------------------------------------------

--
-- Tabellenstruktur für Tabelle `archetypes`
--

CREATE TABLE IF NOT EXISTS `archetypes` (
  `arche_id` tinyint(3) unsigned NOT NULL,
  `arche_name` varchar(50) COLLATE latin1_general_ci NOT NULL,
  `arche_desc` text COLLATE latin1_general_ci,
  PRIMARY KEY (`arche_id`)
) ENGINE=MyISAM DEFAULT CHARSET=latin1 COLLATE=latin1_general_ci;

--
-- Daten für Tabelle `archetypes`
--

INSERT INTO `archetypes` (`arche_id`, `arche_name`, `arche_desc`) VALUES(1, 'Priest', NULL);
INSERT INTO `archetypes` (`arche_id`, `arche_name`, `arche_desc`) VALUES(2, 'Soldier', NULL);
INSERT INTO `archetypes` (`arche_id`, `arche_name`, `arche_desc`) VALUES(3, 'Rogue', NULL);
INSERT INTO `archetypes` (`arche_id`, `arche_name`, `arche_desc`) VALUES(4, 'Mage', NULL);

-- --------------------------------------------------------

--
-- Tabellenstruktur für Tabelle `body`
--

CREATE TABLE IF NOT EXISTS `body` (
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

--
-- Daten für Tabelle `body`
--


-- --------------------------------------------------------

--
-- Tabellenstruktur für Tabelle `characters`
--

CREATE TABLE IF NOT EXISTS `characters` (
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

--
-- Daten für Tabelle `characters`
--

INSERT INTO `characters` (`character_id`, `account_id`, `name`, `race`, `class`, `level`, `sex`, `realm_id`, `map_id`, `language`, `headmesh`, `size`, `voice`, `last_connection`, `pos_x`, `pos_y`, `pos_z`, `lbinprv`) VALUES(2018, 1, 'Conanhimself', 1, 18, 1, 2, 1, 4010, 'en', 793613, 95, 2, '14/05/2010', 1134460201, 1125688809, 1142939586, 1126113);

-- --------------------------------------------------------

--
-- Tabellenstruktur für Tabelle `classes`
--

CREATE TABLE IF NOT EXISTS `classes` (
  `class_id` tinyint(3) unsigned NOT NULL,
  `class_name` varchar(50) COLLATE latin1_general_ci NOT NULL,
  `class_desc` text COLLATE latin1_general_ci,
  `class_client_id` tinyint(3) NOT NULL,
  PRIMARY KEY (`class_id`)
) ENGINE=MyISAM DEFAULT CHARSET=latin1 COLLATE=latin1_general_ci;

--
-- Daten für Tabelle `classes`
--

INSERT INTO `classes` (`class_id`, `class_name`, `class_desc`, `class_client_id`) VALUES(1, 'Priest of Mitra', NULL, 24);
INSERT INTO `classes` (`class_id`, `class_name`, `class_desc`, `class_client_id`) VALUES(2, 'Tempest of Set', NULL, 28);
INSERT INTO `classes` (`class_id`, `class_name`, `class_desc`, `class_client_id`) VALUES(3, 'Bear Shaman', NULL, 29);
INSERT INTO `classes` (`class_id`, `class_name`, `class_desc`, `class_client_id`) VALUES(4, 'Conqueror', NULL, 22);
INSERT INTO `classes` (`class_id`, `class_name`, `class_desc`, `class_client_id`) VALUES(5, 'Dark Templar', NULL, 31);
INSERT INTO `classes` (`class_id`, `class_name`, `class_desc`, `class_client_id`) VALUES(6, 'Guardian', NULL, 20);
INSERT INTO `classes` (`class_id`, `class_name`, `class_desc`, `class_client_id`) VALUES(7, 'Barbarian', NULL, 18);
INSERT INTO `classes` (`class_id`, `class_name`, `class_desc`, `class_client_id`) VALUES(8, 'Ranger', NULL, 39);
INSERT INTO `classes` (`class_id`, `class_name`, `class_desc`, `class_client_id`) VALUES(9, 'Assassin', NULL, 34);
INSERT INTO `classes` (`class_id`, `class_name`, `class_desc`, `class_client_id`) VALUES(10, 'Herald of Xotli', NULL, 43);
INSERT INTO `classes` (`class_id`, `class_name`, `class_desc`, `class_client_id`) VALUES(11, 'Demonologist', NULL, 44);
INSERT INTO `classes` (`class_id`, `class_name`, `class_desc`, `class_client_id`) VALUES(12, 'Necromancer', NULL, 41);

-- --------------------------------------------------------

--
-- Tabellenstruktur für Tabelle `const_abilitys`
--

CREATE TABLE IF NOT EXISTS `const_abilitys` (
  `class` int(12) NOT NULL DEFAULT '0',
  `level` int(12) NOT NULL DEFAULT '0',
  `abilitys` longtext,
  `abilitysNew` longtext,
  `spells` longtext,
  `spellsNew` longtext
) ENGINE=MyISAM DEFAULT CHARSET=latin1;

--
-- Daten für Tabelle `const_abilitys`
--

INSERT INTO `const_abilitys` (`class`, `level`, `abilitys`, `abilitysNew`, `spells`, `spellsNew`) VALUES(29, 1, '769094_1-769099_1-769095_1', NULL, NULL, NULL);
INSERT INTO `const_abilitys` (`class`, `level`, `abilitys`, `abilitysNew`, `spells`, `spellsNew`) VALUES(44, 1, '769094_1-769099_1-769095_1-3675584_1-3316611_1', NULL, '1148653', NULL);
INSERT INTO `const_abilitys` (`class`, `level`, `abilitys`, `abilitysNew`, `spells`, `spellsNew`) VALUES(18, 1, '769094_1-769099_1-769095_1', NULL, NULL, NULL);
INSERT INTO `const_abilitys` (`class`, `level`, `abilitys`, `abilitysNew`, `spells`, `spellsNew`) VALUES(22, 1, '769094_1-769099_1-769095_1', NULL, NULL, NULL);
INSERT INTO `const_abilitys` (`class`, `level`, `abilitys`, `abilitysNew`, `spells`, `spellsNew`) VALUES(44, 2, '769094_1-769099_1-769095_1-3675584_1-3316611_1', NULL, '1148653-1148654', '1148654');

-- --------------------------------------------------------

--
-- Tabellenstruktur für Tabelle `const_stats`
--

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

--
-- Daten für Tabelle `const_stats`
--

INSERT INTO `const_stats` (`level`, `race`, `class`, `health`, `mana`, `stamina`, `exp`, `S`, `I`, `C`, `D`, `W`, `expOffset`) VALUES(1, 1, 29, 118, 171, 176, 100, 1, 1, 1, 1, 1, 0);
INSERT INTO `const_stats` (`level`, `race`, `class`, `health`, `mana`, `stamina`, `exp`, `S`, `I`, `C`, `D`, `W`, `expOffset`) VALUES(1, 2, 28, 118, 0, 176, 100, 1, 1, 1, 1, 1, 0);
INSERT INTO `const_stats` (`level`, `race`, `class`, `health`, `mana`, `stamina`, `exp`, `S`, `I`, `C`, `D`, `W`, `expOffset`) VALUES(1, 2, 24, 118, 0, 176, 100, 1, 1, 1, 1, 1, 0);
INSERT INTO `const_stats` (`level`, `race`, `class`, `health`, `mana`, `stamina`, `exp`, `S`, `I`, `C`, `D`, `W`, `expOffset`) VALUES(1, 2, 22, 118, 0, 176, 100, 1, 1, 1, 1, 1, 0);
INSERT INTO `const_stats` (`level`, `race`, `class`, `health`, `mana`, `stamina`, `exp`, `S`, `I`, `C`, `D`, `W`, `expOffset`) VALUES(1, 1, 20, 130, 0, 179, 100, 1, 1, 1, 1, 1, 0);
INSERT INTO `const_stats` (`level`, `race`, `class`, `health`, `mana`, `stamina`, `exp`, `S`, `I`, `C`, `D`, `W`, `expOffset`) VALUES(1, 1, 18, 118, 0, 176, 100, 1, 1, 1, 1, 1, 0);
INSERT INTO `const_stats` (`level`, `race`, `class`, `health`, `mana`, `stamina`, `exp`, `S`, `I`, `C`, `D`, `W`, `expOffset`) VALUES(1, 1, 31, 118, 0, 176, 100, 1, 1, 1, 1, 1, 0);
INSERT INTO `const_stats` (`level`, `race`, `class`, `health`, `mana`, `stamina`, `exp`, `S`, `I`, `C`, `D`, `W`, `expOffset`) VALUES(1, 1, 34, 118, 0, 176, 100, 1, 1, 1, 1, 1, 0);
INSERT INTO `const_stats` (`level`, `race`, `class`, `health`, `mana`, `stamina`, `exp`, `S`, `I`, `C`, `D`, `W`, `expOffset`) VALUES(1, 1, 39, 133, 0, 213, 100, 1, 1, 1, 1, 1, 0);
INSERT INTO `const_stats` (`level`, `race`, `class`, `health`, `mana`, `stamina`, `exp`, `S`, `I`, `C`, `D`, `W`, `expOffset`) VALUES(1, 1, 41, 118, 0, 176, 100, 1, 1, 1, 1, 1, 0);
INSERT INTO `const_stats` (`level`, `race`, `class`, `health`, `mana`, `stamina`, `exp`, `S`, `I`, `C`, `D`, `W`, `expOffset`) VALUES(1, 1, 43, 118, 0, 176, 100, 1, 1, 1, 1, 1, 0);
INSERT INTO `const_stats` (`level`, `race`, `class`, `health`, `mana`, `stamina`, `exp`, `S`, `I`, `C`, `D`, `W`, `expOffset`) VALUES(1, 1, 44, 118, 0, 176, 100, 1, 1, 1, 1, 1, 0);
INSERT INTO `const_stats` (`level`, `race`, `class`, `health`, `mana`, `stamina`, `exp`, `S`, `I`, `C`, `D`, `W`, `expOffset`) VALUES(1, 2, 18, 118, 0, 176, 100, 1, 1, 1, 1, 1, 0);
INSERT INTO `const_stats` (`level`, `race`, `class`, `health`, `mana`, `stamina`, `exp`, `S`, `I`, `C`, `D`, `W`, `expOffset`) VALUES(1, 2, 20, 118, 0, 176, 100, 1, 1, 1, 1, 1, 0);
INSERT INTO `const_stats` (`level`, `race`, `class`, `health`, `mana`, `stamina`, `exp`, `S`, `I`, `C`, `D`, `W`, `expOffset`) VALUES(1, 1, 22, 118, 0, 176, 100, 1, 1, 1, 1, 1, 0);
INSERT INTO `const_stats` (`level`, `race`, `class`, `health`, `mana`, `stamina`, `exp`, `S`, `I`, `C`, `D`, `W`, `expOffset`) VALUES(1, 1, 24, 118, 0, 176, 100, 1, 1, 1, 1, 1, 0);
INSERT INTO `const_stats` (`level`, `race`, `class`, `health`, `mana`, `stamina`, `exp`, `S`, `I`, `C`, `D`, `W`, `expOffset`) VALUES(1, 1, 28, 118, 0, 176, 100, 1, 1, 1, 1, 1, 0);
INSERT INTO `const_stats` (`level`, `race`, `class`, `health`, `mana`, `stamina`, `exp`, `S`, `I`, `C`, `D`, `W`, `expOffset`) VALUES(1, 2, 29, 108, 171, 178, 100, 1, 1, 1, 1, 1, 0);
INSERT INTO `const_stats` (`level`, `race`, `class`, `health`, `mana`, `stamina`, `exp`, `S`, `I`, `C`, `D`, `W`, `expOffset`) VALUES(1, 2, 31, 118, 0, 176, 100, 1, 1, 1, 1, 1, 0);
INSERT INTO `const_stats` (`level`, `race`, `class`, `health`, `mana`, `stamina`, `exp`, `S`, `I`, `C`, `D`, `W`, `expOffset`) VALUES(1, 2, 34, 118, 0, 176, 100, 1, 1, 1, 1, 1, 0);
INSERT INTO `const_stats` (`level`, `race`, `class`, `health`, `mana`, `stamina`, `exp`, `S`, `I`, `C`, `D`, `W`, `expOffset`) VALUES(1, 2, 39, 133, 0, 213, 100, 1, 1, 1, 1, 1, 0);
INSERT INTO `const_stats` (`level`, `race`, `class`, `health`, `mana`, `stamina`, `exp`, `S`, `I`, `C`, `D`, `W`, `expOffset`) VALUES(1, 2, 41, 118, 0, 176, 100, 1, 1, 1, 1, 1, 0);
INSERT INTO `const_stats` (`level`, `race`, `class`, `health`, `mana`, `stamina`, `exp`, `S`, `I`, `C`, `D`, `W`, `expOffset`) VALUES(1, 2, 43, 118, 0, 176, 100, 1, 1, 1, 1, 1, 0);
INSERT INTO `const_stats` (`level`, `race`, `class`, `health`, `mana`, `stamina`, `exp`, `S`, `I`, `C`, `D`, `W`, `expOffset`) VALUES(1, 2, 44, 118, 0, 176, 100, 1, 1, 1, 1, 1, 0);
INSERT INTO `const_stats` (`level`, `race`, `class`, `health`, `mana`, `stamina`, `exp`, `S`, `I`, `C`, `D`, `W`, `expOffset`) VALUES(1, 3, 18, 118, 0, 176, 100, 1, 1, 1, 1, 1, 0);
INSERT INTO `const_stats` (`level`, `race`, `class`, `health`, `mana`, `stamina`, `exp`, `S`, `I`, `C`, `D`, `W`, `expOffset`) VALUES(1, 3, 20, 118, 0, 176, 100, 1, 1, 1, 1, 1, 0);
INSERT INTO `const_stats` (`level`, `race`, `class`, `health`, `mana`, `stamina`, `exp`, `S`, `I`, `C`, `D`, `W`, `expOffset`) VALUES(1, 3, 22, 118, 0, 176, 100, 1, 1, 1, 1, 1, 0);
INSERT INTO `const_stats` (`level`, `race`, `class`, `health`, `mana`, `stamina`, `exp`, `S`, `I`, `C`, `D`, `W`, `expOffset`) VALUES(1, 3, 24, 118, 0, 176, 100, 1, 1, 1, 1, 1, 0);
INSERT INTO `const_stats` (`level`, `race`, `class`, `health`, `mana`, `stamina`, `exp`, `S`, `I`, `C`, `D`, `W`, `expOffset`) VALUES(1, 3, 28, 118, 0, 176, 100, 1, 1, 1, 1, 1, 0);
INSERT INTO `const_stats` (`level`, `race`, `class`, `health`, `mana`, `stamina`, `exp`, `S`, `I`, `C`, `D`, `W`, `expOffset`) VALUES(1, 3, 29, 118, 0, 176, 100, 1, 1, 1, 1, 1, 0);
INSERT INTO `const_stats` (`level`, `race`, `class`, `health`, `mana`, `stamina`, `exp`, `S`, `I`, `C`, `D`, `W`, `expOffset`) VALUES(1, 3, 31, 118, 0, 176, 100, 1, 1, 1, 1, 1, 0);
INSERT INTO `const_stats` (`level`, `race`, `class`, `health`, `mana`, `stamina`, `exp`, `S`, `I`, `C`, `D`, `W`, `expOffset`) VALUES(1, 3, 34, 118, 0, 176, 100, 1, 1, 1, 1, 1, 0);
INSERT INTO `const_stats` (`level`, `race`, `class`, `health`, `mana`, `stamina`, `exp`, `S`, `I`, `C`, `D`, `W`, `expOffset`) VALUES(1, 3, 39, 118, 0, 176, 100, 1, 1, 1, 1, 1, 0);
INSERT INTO `const_stats` (`level`, `race`, `class`, `health`, `mana`, `stamina`, `exp`, `S`, `I`, `C`, `D`, `W`, `expOffset`) VALUES(1, 3, 41, 89, 246, 106, 100, 1, 1, 1, 1, 1, 0);
INSERT INTO `const_stats` (`level`, `race`, `class`, `health`, `mana`, `stamina`, `exp`, `S`, `I`, `C`, `D`, `W`, `expOffset`) VALUES(1, 3, 43, 118, 0, 176, 100, 1, 1, 1, 1, 1, 0);
INSERT INTO `const_stats` (`level`, `race`, `class`, `health`, `mana`, `stamina`, `exp`, `S`, `I`, `C`, `D`, `W`, `expOffset`) VALUES(1, 3, 44, 118, 200, 176, 100, 1, 1, 1, 1, 1, 0);
INSERT INTO `const_stats` (`level`, `race`, `class`, `health`, `mana`, `stamina`, `exp`, `S`, `I`, `C`, `D`, `W`, `expOffset`) VALUES(2, 1, 18, 163, 0, 218, 340, 1, 1, 1, 1, 1, 100);
INSERT INTO `const_stats` (`level`, `race`, `class`, `health`, `mana`, `stamina`, `exp`, `S`, `I`, `C`, `D`, `W`, `expOffset`) VALUES(3, 1, 18, 216, 0, 262, 400, 1, 1, 1, 1, 1, 0);
INSERT INTO `const_stats` (`level`, `race`, `class`, `health`, `mana`, `stamina`, `exp`, `S`, `I`, `C`, `D`, `W`, `expOffset`) VALUES(2, 1, 29, 136, 209, 203, 340, 1, 1, 1, 1, 1, 100);
INSERT INTO `const_stats` (`level`, `race`, `class`, `health`, `mana`, `stamina`, `exp`, `S`, `I`, `C`, `D`, `W`, `expOffset`) VALUES(2, 1, 20, 195, 0, 218, 340, 1, 1, 1, 1, 1, 100);
INSERT INTO `const_stats` (`level`, `race`, `class`, `health`, `mana`, `stamina`, `exp`, `S`, `I`, `C`, `D`, `W`, `expOffset`) VALUES(2, 2, 39, 174, 0, 253, 340, 3, 2, 2, 4, 2, 100);
INSERT INTO `const_stats` (`level`, `race`, `class`, `health`, `mana`, `stamina`, `exp`, `S`, `I`, `C`, `D`, `W`, `expOffset`) VALUES(3, 2, 39, 222, 0, 291, 500, 4, 2, 4, 6, 3, 0);
INSERT INTO `const_stats` (`level`, `race`, `class`, `health`, `mana`, `stamina`, `exp`, `S`, `I`, `C`, `D`, `W`, `expOffset`) VALUES(4, 2, 39, 261, 0, 331, 1000, 6, 3, 5, 9, 4, 0);
INSERT INTO `const_stats` (`level`, `race`, `class`, `health`, `mana`, `stamina`, `exp`, `S`, `I`, `C`, `D`, `W`, `expOffset`) VALUES(5, 2, 39, 309, 0, 371, 1500, 7, 4, 7, 12, 6, 0);
INSERT INTO `const_stats` (`level`, `race`, `class`, `health`, `mana`, `stamina`, `exp`, `S`, `I`, `C`, `D`, `W`, `expOffset`) VALUES(2, 2, 29, 136, 209, 203, 340, 3, 3, 2, 2, 3, 100);
INSERT INTO `const_stats` (`level`, `race`, `class`, `health`, `mana`, `stamina`, `exp`, `S`, `I`, `C`, `D`, `W`, `expOffset`) VALUES(3, 2, 29, 172, 247, 234, 300, 6, 4, 4, 4, 6, 0);
INSERT INTO `const_stats` (`level`, `race`, `class`, `health`, `mana`, `stamina`, `exp`, `S`, `I`, `C`, `D`, `W`, `expOffset`) VALUES(4, 2, 29, 200, 285, 259, 1000, 8, 6, 5, 5, 8, 0);
INSERT INTO `const_stats` (`level`, `race`, `class`, `health`, `mana`, `stamina`, `exp`, `S`, `I`, `C`, `D`, `W`, `expOffset`) VALUES(5, 2, 29, 237, 323, 290, 1500, 11, 7, 7, 7, 11, 0);
INSERT INTO `const_stats` (`level`, `race`, `class`, `health`, `mana`, `stamina`, `exp`, `S`, `I`, `C`, `D`, `W`, `expOffset`) VALUES(2, 3, 41, 127, 288, 122, 340, 2, 4, 2, 2, 2, 100);
INSERT INTO `const_stats` (`level`, `race`, `class`, `health`, `mana`, `stamina`, `exp`, `S`, `I`, `C`, `D`, `W`, `expOffset`) VALUES(3, 3, 41, 157, 333, 138, 500, 3, 7, 2, 4, 4, 0);
INSERT INTO `const_stats` (`level`, `race`, `class`, `health`, `mana`, `stamina`, `exp`, `S`, `I`, `C`, `D`, `W`, `expOffset`) VALUES(4, 3, 41, 194, 375, 154, 1000, 4, 10, 3, 5, 5, 0);
INSERT INTO `const_stats` (`level`, `race`, `class`, `health`, `mana`, `stamina`, `exp`, `S`, `I`, `C`, `D`, `W`, `expOffset`) VALUES(5, 3, 41, 232, 420, 170, 1500, 4, 13, 3, 7, 7, 0);
INSERT INTO `const_stats` (`level`, `race`, `class`, `health`, `mana`, `stamina`, `exp`, `S`, `I`, `C`, `D`, `W`, `expOffset`) VALUES(6, 3, 41, 270, 459, 186, 2000, 5, 15, 5, 8, 8, 0);
INSERT INTO `const_stats` (`level`, `race`, `class`, `health`, `mana`, `stamina`, `exp`, `S`, `I`, `C`, `D`, `W`, `expOffset`) VALUES(7, 3, 41, 300, 501, 200, 5000, 6, 18, 5, 9, 9, 0);
INSERT INTO `const_stats` (`level`, `race`, `class`, `health`, `mana`, `stamina`, `exp`, `S`, `I`, `C`, `D`, `W`, `expOffset`) VALUES(8, 3, 41, 346, 546, 220, 10000, 7, 21, 7, 11, 11, 0);
INSERT INTO `const_stats` (`level`, `race`, `class`, `health`, `mana`, `stamina`, `exp`, `S`, `I`, `C`, `D`, `W`, `expOffset`) VALUES(9, 3, 41, 376, 588, 234, 15000, 8, 24, 7, 12, 12, 0);
INSERT INTO `const_stats` (`level`, `race`, `class`, `health`, `mana`, `stamina`, `exp`, `S`, `I`, `C`, `D`, `W`, `expOffset`) VALUES(10, 3, 41, 416, 630, 250, 20000, 9, 27, 8, 13, 13, 0);
INSERT INTO `const_stats` (`level`, `race`, `class`, `health`, `mana`, `stamina`, `exp`, `S`, `I`, `C`, `D`, `W`, `expOffset`) VALUES(11, 3, 41, 447, 675, 266, 30000, 10, 30, 8, 15, 15, 0);
INSERT INTO `const_stats` (`level`, `race`, `class`, `health`, `mana`, `stamina`, `exp`, `S`, `I`, `C`, `D`, `W`, `expOffset`) VALUES(12, 3, 41, 489, 717, 290, 50000, 11, 33, 9, 16, 16, 0);
INSERT INTO `const_stats` (`level`, `race`, `class`, `health`, `mana`, `stamina`, `exp`, `S`, `I`, `C`, `D`, `W`, `expOffset`) VALUES(13, 3, 41, 557, 762, 306, 75000, 11, 36, 14, 18, 18, 0);
INSERT INTO `const_stats` (`level`, `race`, `class`, `health`, `mana`, `stamina`, `exp`, `S`, `I`, `C`, `D`, `W`, `expOffset`) VALUES(14, 3, 41, 596, 804, 322, 100000, 12, 39, 15, 19, 19, 0);
INSERT INTO `const_stats` (`level`, `race`, `class`, `health`, `mana`, `stamina`, `exp`, `S`, `I`, `C`, `D`, `W`, `expOffset`) VALUES(15, 3, 41, 627, 846, 336, 200000, 13, 42, 15, 20, 20, 0);

-- --------------------------------------------------------

--
-- Tabellenstruktur für Tabelle `face`
--

CREATE TABLE IF NOT EXISTS `face` (
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

--
-- Daten für Tabelle `face`
--


-- --------------------------------------------------------

--
-- Tabellenstruktur für Tabelle `maps`
--

CREATE TABLE IF NOT EXISTS `maps` (
  `map_id` int(11) NOT NULL,
  `map_name` varchar(150) COLLATE latin1_general_ci DEFAULT NULL,
  `position_0` int(11) NOT NULL,
  `position_1` int(11) NOT NULL,
  `position_2` int(11) NOT NULL,
  `rotation_0` int(11) NOT NULL,
  `rotation_1` int(11) NOT NULL,
  `rotation_2` int(11) NOT NULL,
  PRIMARY KEY (`map_id`)
) ENGINE=MyISAM DEFAULT CHARSET=latin1 COLLATE=latin1_general_ci;

--
-- Daten für Tabelle `maps`
--

INSERT INTO `maps` (`map_id`, `map_name`, `position_0`, `position_1`, `position_2`, `rotation_0`, `rotation_1`, `rotation_2`) VALUES(4010, 'Tortage Beach', 1134641152, 1125207987, 1144011162, 0, 0, 0);
INSERT INTO `maps` (`map_id`, `map_name`, `position_0`, `position_1`, `position_2`, `rotation_0`, `rotation_1`, `rotation_2`) VALUES(4025, 'Acheronian Ruins', 1114187917, 1124053233, 1134462075, 1056274244, 1063299392, 1793);
INSERT INTO `maps` (`map_id`, `map_name`, `position_0`, `position_1`, `position_2`, `rotation_0`, `rotation_1`, `rotation_2`) VALUES(500, 'GM Island', 1133136232, 1102708380, 1135420002, 0, 0, 0);
INSERT INTO `maps` (`map_id`, `map_name`, `position_0`, `position_1`, `position_2`, `rotation_0`, `rotation_1`, `rotation_2`) VALUES(2070, 'Poitain', 1151341050, 1127124818, 1152928673, 0, 0, 0);
INSERT INTO `maps` (`map_id`, `map_name`, `position_0`, `position_1`, `position_2`, `rotation_0`, `rotation_1`, `rotation_2`) VALUES(1000, 'Conarch Village', 1114641152, 1114053233, 1144011162, 0, 0, 0);
INSERT INTO `maps` (`map_id`, `map_name`, `position_0`, `position_1`, `position_2`, `rotation_0`, `rotation_1`, `rotation_2`) VALUES(2000, 'Old Tarantia', 1146428220, 1120564870, 1140034652, 0, 0, 0);
INSERT INTO `maps` (`map_id`, `map_name`, `position_0`, `position_1`, `position_2`, `rotation_0`, `rotation_1`, `rotation_2`) VALUES(3020, 'Khopshef Province', 1141409216, 1094408432, 1149125668, 0, 0, 0);
INSERT INTO `maps` (`map_id`, `map_name`, `position_0`, `position_1`, `position_2`, `rotation_0`, `rotation_1`, `rotation_2`) VALUES(6400, 'Paikang', 1140067160, 1123232140, 1148852102, 0, 0, 0);
INSERT INTO `maps` (`map_id`, `map_name`, `position_0`, `position_1`, `position_2`, `rotation_0`, `rotation_1`, `rotation_2`) VALUES(6300, 'Chosain Province', 1149755208, 1134385155, 1145405093, 0, 0, 0);

-- --------------------------------------------------------

--
-- Tabellenstruktur für Tabelle `npcs`
--

CREATE TABLE IF NOT EXISTS `npcs` (
  `id` int(11) NOT NULL,
  `name` varchar(255) NOT NULL,
  `nameId` bigint(12) NOT NULL DEFAULT '0',
  `map` int(11) NOT NULL,
  `action` int(11) NOT NULL,
  `level` int(11) NOT NULL,
  `fraction` int(11) NOT NULL DEFAULT '0',
  `health` int(11) NOT NULL DEFAULT '0',
  `stamina` int(11) NOT NULL DEFAULT '0',
  `mana` int(11) NOT NULL DEFAULT '0',
  `getExp` int(11) NOT NULL DEFAULT '0',
  `extras` longtext NOT NULL,
  PRIMARY KEY (`id`)
) ENGINE=MyISAM DEFAULT CHARSET=latin1;

--
-- Daten für Tabelle `npcs`
--

INSERT INTO `npcs` (`id`, `name`, `nameId`, `map`, `action`, `level`, `fraction`, `health`, `stamina`, `mana`, `getExp`, `extras`) VALUES(433433, 'Scavenger Lackey', 12141, 4010, 3, 1, 1, 59, 59, 59, 50, '');
INSERT INTO `npcs` (`id`, `name`, `nameId`, `map`, `action`, `level`, `fraction`, `health`, `stamina`, `mana`, `getExp`, `extras`) VALUES(433435, 'Scavenger Lackey', 12141, 4010, 3, 1, 1, 59, 59, 59, 50, '');
INSERT INTO `npcs` (`id`, `name`, `nameId`, `map`, `action`, `level`, `fraction`, `health`, `stamina`, `mana`, `getExp`, `extras`) VALUES(523742, 'Kalanthes', 2704, 4010, 1, 80, 0, 4605, 4605, 4605, 0, '');
INSERT INTO `npcs` (`id`, `name`, `nameId`, `map`, `action`, `level`, `fraction`, `health`, `stamina`, `mana`, `getExp`, `extras`) VALUES(483586, 'Pillager', 8756, 4010, 3, 1, 0, 63, 6300, 6300, 10, '');
INSERT INTO `npcs` (`id`, `name`, `nameId`, `map`, `action`, `level`, `fraction`, `health`, `stamina`, `mana`, `getExp`, `extras`) VALUES(118277, 'Stygian Female', 2676, 4000, 0, 1, 0, 6300, 6300, 6300, 0, '');
INSERT INTO `npcs` (`id`, `name`, `nameId`, `map`, `action`, `level`, `fraction`, `health`, `stamina`, `mana`, `getExp`, `extras`) VALUES(118278, 'Saddur', 2993, 4000, 3, 1, 0, 6300, 6300, 6300, 0, '');
INSERT INTO `npcs` (`id`, `name`, `nameId`, `map`, `action`, `level`, `fraction`, `health`, `stamina`, `mana`, `getExp`, `extras`) VALUES(118280, 'Female Template', 2680, 4000, 0, 1, 0, 6300, 6300, 6300, 0, '');
INSERT INTO `npcs` (`id`, `name`, `nameId`, `map`, `action`, `level`, `fraction`, `health`, `stamina`, `mana`, `getExp`, `extras`) VALUES(28301, 'Crocodile', 14049, 4010, 3, 1, 1, 63, 63, 63, 50, '');
INSERT INTO `npcs` (`id`, `name`, `nameId`, `map`, `action`, `level`, `fraction`, `health`, `stamina`, `mana`, `getExp`, `extras`) VALUES(433474, 'Casilda', 9411, 4010, 1, 1, 0, 59, 59, 59, 0, '');

-- --------------------------------------------------------

--
-- Tabellenstruktur für Tabelle `npc_details`
--

CREATE TABLE IF NOT EXISTS `npc_details` (
  `id` int(11) NOT NULL DEFAULT '0',
  `position_x` bigint(12) NOT NULL DEFAULT '0',
  `position_y` bigint(12) NOT NULL DEFAULT '0',
  `position_z` bigint(12) NOT NULL DEFAULT '0',
  `rotation_x` bigint(12) NOT NULL DEFAULT '0',
  `rotation_y` bigint(12) NOT NULL DEFAULT '0',
  `rotation_z` bigint(12) NOT NULL DEFAULT '0',
  `bodymesh` int(11) NOT NULL DEFAULT '0',
  `headmesh` int(11) NOT NULL DEFAULT '0',
  `hairmesh` int(11) NOT NULL DEFAULT '0',
  `beardmesh` int(11) NOT NULL DEFAULT '0',
  `npcsize` int(11) NOT NULL DEFAULT '0',
  `gender` int(11) NOT NULL DEFAULT '2',
  `race` int(11) NOT NULL DEFAULT '1',
  `type` int(11) NOT NULL DEFAULT '50' COMMENT '0x32 = humen; 0x6e = monster',
  PRIMARY KEY (`id`)
) ENGINE=MyISAM DEFAULT CHARSET=latin1;

--
-- Daten für Tabelle `npc_details`
--

INSERT INTO `npc_details` (`id`, `position_x`, `position_y`, `position_z`, `rotation_x`, `rotation_y`, `rotation_z`, `bodymesh`, `headmesh`, `hairmesh`, `beardmesh`, `npcsize`, `gender`, `race`, `type`) VALUES(483586, 1136315007, 1125769650, 1143649108, 1065352577, 1007614426, 3584, 137506, 793580, 0, 0, 96, 2, 11, 50);
INSERT INTO `npc_details` (`id`, `position_x`, `position_y`, `position_z`, `rotation_x`, `rotation_y`, `rotation_z`, `bodymesh`, `headmesh`, `hairmesh`, `beardmesh`, `npcsize`, `gender`, `race`, `type`) VALUES(28301, 1131571839, 1125218145, 1144300667, 1059595942, 1057713526, 0, 16604, 0, 0, 0, 130, 0, 64, 110);
INSERT INTO `npc_details` (`id`, `position_x`, `position_y`, `position_z`, `rotation_x`, `rotation_y`, `rotation_z`, `bodymesh`, `headmesh`, `hairmesh`, `beardmesh`, `npcsize`, `gender`, `race`, `type`) VALUES(523742, 1134991770, 1125272562, 1144029610, 1059471930, 3208817143, 0, 168152, 160655, 0, 0, 100, 2, 11, 50);
INSERT INTO `npc_details` (`id`, `position_x`, `position_y`, `position_z`, `rotation_x`, `rotation_y`, `rotation_z`, `bodymesh`, `headmesh`, `hairmesh`, `beardmesh`, `npcsize`, `gender`, `race`, `type`) VALUES(433474, 1135923790, 1125734178, 1144220353, 1063106982, 3204443122, 85760, 701595, 222567, 0, 0, 100, 2, 11, 50);
INSERT INTO `npc_details` (`id`, `position_x`, `position_y`, `position_z`, `rotation_x`, `rotation_y`, `rotation_z`, `bodymesh`, `headmesh`, `hairmesh`, `beardmesh`, `npcsize`, `gender`, `race`, `type`) VALUES(433433, 1134021280, 1125247291, 1143100195, 1054102189, 1063842617, 0, 168152, 160655, 0, 0, 100, 3, 2, 50);
INSERT INTO `npc_details` (`id`, `position_x`, `position_y`, `position_z`, `rotation_x`, `rotation_y`, `rotation_z`, `bodymesh`, `headmesh`, `hairmesh`, `beardmesh`, `npcsize`, `gender`, `race`, `type`) VALUES(433435, 1134607368, 1125238684, 1143222632, 1058905085, 3209280275, 0, 140327, 191546, 13, 0, 100, 2, 1, 50);
INSERT INTO `npc_details` (`id`, `position_x`, `position_y`, `position_z`, `rotation_x`, `rotation_y`, `rotation_z`, `bodymesh`, `headmesh`, `hairmesh`, `beardmesh`, `npcsize`, `gender`, `race`, `type`) VALUES(118277, 1132689195, 1086354393, 1133280297, 1065146661, 1042296925, 0, 137507, 206707, 0, 0, 90, 3, 3, 50);
INSERT INTO `npc_details` (`id`, `position_x`, `position_y`, `position_z`, `rotation_x`, `rotation_y`, `rotation_z`, `bodymesh`, `headmesh`, `hairmesh`, `beardmesh`, `npcsize`, `gender`, `race`, `type`) VALUES(118278, 1132708332, 1087467684, 1133398688, 0, 1065353216, 0, 3274147, 3274160, 0, 0, 105, 2, 3, 50);
INSERT INTO `npc_details` (`id`, `position_x`, `position_y`, `position_z`, `rotation_x`, `rotation_y`, `rotation_z`, `bodymesh`, `headmesh`, `hairmesh`, `beardmesh`, `npcsize`, `gender`, `race`, `type`) VALUES(118280, 1132734874, 1086351140, 1133153944, 0, 1065353216, 0, 18309, 205450, 0, 0, 100, 3, 1, 50);

-- --------------------------------------------------------

--
-- Tabellenstruktur für Tabelle `npc_items`
--

CREATE TABLE IF NOT EXISTS `npc_items` (
  `id` int(11) NOT NULL DEFAULT '0' COMMENT 'ID of npc',
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
  PRIMARY KEY (`id`)
) ENGINE=MyISAM DEFAULT CHARSET=latin1;

--
-- Daten für Tabelle `npc_items`
--

INSERT INTO `npc_items` (`id`, `neck`, `head`, `tokens`, `chest`, `main_hand_left`, `belt`, `off_hand_left`, `ring_left`, `ring_right`, `cloak`, `shoulder`, `boots`, `hands`, `legs`, `first_pos_bag`, `wrist`, `main_hand_right`, `off_hand_right`) VALUES(28301, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
INSERT INTO `npc_items` (`id`, `neck`, `head`, `tokens`, `chest`, `main_hand_left`, `belt`, `off_hand_left`, `ring_left`, `ring_right`, `cloak`, `shoulder`, `boots`, `hands`, `legs`, `first_pos_bag`, `wrist`, `main_hand_right`, `off_hand_right`) VALUES(433474, 0, 0, 0, 189905, 195325, 194918, 195325, 0, 0, 0, 0, 189896, 0, 193680, 0, 116743, 0, 0);
INSERT INTO `npc_items` (`id`, `neck`, `head`, `tokens`, `chest`, `main_hand_left`, `belt`, `off_hand_left`, `ring_left`, `ring_right`, `cloak`, `shoulder`, `boots`, `hands`, `legs`, `first_pos_bag`, `wrist`, `main_hand_right`, `off_hand_right`) VALUES(523742, 0, 0, 0, 189905, 195325, 0, 0, 0, 0, 0, 190411, 0, 0, 189901, 0, 190410, 0, 0);
INSERT INTO `npc_items` (`id`, `neck`, `head`, `tokens`, `chest`, `main_hand_left`, `belt`, `off_hand_left`, `ring_left`, `ring_right`, `cloak`, `shoulder`, `boots`, `hands`, `legs`, `first_pos_bag`, `wrist`, `main_hand_right`, `off_hand_right`) VALUES(433435, 0, 0, 0, 189905, 195325, 194918, 195325, 0, 0, 0, 0, 0, 0, 193680, 0, 0, 0, 0);
INSERT INTO `npc_items` (`id`, `neck`, `head`, `tokens`, `chest`, `main_hand_left`, `belt`, `off_hand_left`, `ring_left`, `ring_right`, `cloak`, `shoulder`, `boots`, `hands`, `legs`, `first_pos_bag`, `wrist`, `main_hand_right`, `off_hand_right`) VALUES(433433, 0, 0, 0, 189905, 195325, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
INSERT INTO `npc_items` (`id`, `neck`, `head`, `tokens`, `chest`, `main_hand_left`, `belt`, `off_hand_left`, `ring_left`, `ring_right`, `cloak`, `shoulder`, `boots`, `hands`, `legs`, `first_pos_bag`, `wrist`, `main_hand_right`, `off_hand_right`) VALUES(118277, 0, 0, 0, 105844, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
INSERT INTO `npc_items` (`id`, `neck`, `head`, `tokens`, `chest`, `main_hand_left`, `belt`, `off_hand_left`, `ring_left`, `ring_right`, `cloak`, `shoulder`, `boots`, `hands`, `legs`, `first_pos_bag`, `wrist`, `main_hand_right`, `off_hand_right`) VALUES(118278, 0, 0, 0, 0, 261174, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
INSERT INTO `npc_items` (`id`, `neck`, `head`, `tokens`, `chest`, `main_hand_left`, `belt`, `off_hand_left`, `ring_left`, `ring_right`, `cloak`, `shoulder`, `boots`, `hands`, `legs`, `first_pos_bag`, `wrist`, `main_hand_right`, `off_hand_right`) VALUES(118280, 0, 0, 0, 324140, 0, 0, 0, 0, 0, 0, 0, 0, 0, 324168, 0, 3388437, 0, 0);

-- --------------------------------------------------------

--
-- Tabellenstruktur für Tabelle `playerbar`
--

CREATE TABLE IF NOT EXISTS `playerbar` (
  `id` bigint(255) NOT NULL DEFAULT '0' COMMENT 'character id',
  `bar0` bigint(255) NOT NULL DEFAULT '0' COMMENT '0x00',
  `bar1` bigint(255) NOT NULL DEFAULT '0' COMMENT '0x01',
  `bar2` bigint(255) NOT NULL DEFAULT '0' COMMENT '0x02',
  `bar3` bigint(255) NOT NULL DEFAULT '0' COMMENT '0x0a',
  `bar4` bigint(255) NOT NULL DEFAULT '0' COMMENT '0x0c',
  `bar5` bigint(255) NOT NULL DEFAULT '0' COMMENT '0x64',
  `bar6` bigint(255) NOT NULL DEFAULT '0' COMMENT '0x65',
  `bar7` bigint(255) NOT NULL DEFAULT '0' COMMENT '0x66',
  `bar8` bigint(255) NOT NULL DEFAULT '0' COMMENT '0x67',
  `bar9` bigint(255) NOT NULL DEFAULT '0' COMMENT '0x6d',
  `bar10` bigint(255) NOT NULL DEFAULT '0' COMMENT '0x6e',
  `bar11` bigint(255) NOT NULL DEFAULT '0' COMMENT '0x70',
  `bar12` bigint(255) NOT NULL DEFAULT '0' COMMENT '0x4e84'
) ENGINE=MyISAM DEFAULT CHARSET=latin1;

--
-- Daten für Tabelle `playerbar`
--

INSERT INTO `playerbar` (`id`, `bar0`, `bar1`, `bar2`, `bar3`, `bar4`, `bar5`, `bar6`, `bar7`, `bar8`, `bar9`, `bar10`, `bar11`, `bar12`) VALUES(2020, 769094, 769099, 769095, 0, 0, 769094, 769099, 769095, 1148653, 0, 0, 0, 0);

-- --------------------------------------------------------

--
-- Tabellenstruktur für Tabelle `playerdata`
--

CREATE TABLE IF NOT EXISTS `playerdata` (
  `character_id` bigint(255) NOT NULL DEFAULT '0',
  `account_id` bigint(255) NOT NULL DEFAULT '0',
  `posX` bigint(255) NOT NULL DEFAULT '0',
  `posY` bigint(255) NOT NULL DEFAULT '0',
  `posZ` bigint(255) NOT NULL DEFAULT '0',
  `rotX` bigint(255) NOT NULL DEFAULT '0',
  `rotY` bigint(255) NOT NULL DEFAULT '0',
  `rotZ` bigint(255) NOT NULL DEFAULT '0'
) ENGINE=MyISAM DEFAULT CHARSET=latin1;

--
-- Daten für Tabelle `playerdata`
--

INSERT INTO `playerdata` (`character_id`, `account_id`, `posX`, `posY`, `posZ`, `rotX`, `rotY`, `rotZ`) VALUES(2020, 2, 1134706428, 1125252603, 1143209945, 3210327966, 1057401534, 1046696952);
INSERT INTO `playerdata` (`character_id`, `account_id`, `posX`, `posY`, `posZ`, `rotX`, `rotY`, `rotZ`) VALUES(2021, 2, 1135080465, 1125325996, 1143947714, 3188982383, 1065177037, 1043207292);
INSERT INTO `playerdata` (`character_id`, `account_id`, `posX`, `posY`, `posZ`, `rotX`, `rotY`, `rotZ`) VALUES(2033, 2, 0, 0, 0, 3452816845, 3452816845, 3452816845);
INSERT INTO `playerdata` (`character_id`, `account_id`, `posX`, `posY`, `posZ`, `rotX`, `rotY`, `rotZ`) VALUES(2034, 1, 0, 0, 0, 3452816845, 3452816845, 3452816845);

-- --------------------------------------------------------

--
-- Tabellenstruktur für Tabelle `playerstats`
--

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

--
-- Daten für Tabelle `playerstats`
--


-- --------------------------------------------------------

--
-- Tabellenstruktur für Tabelle `player_bar`
--

CREATE TABLE IF NOT EXISTS `player_bar` (
  `id` bigint(255) NOT NULL DEFAULT '0',
  `bar0` bigint(255) NOT NULL DEFAULT '0' COMMENT '0x64',
  `bar1` bigint(255) NOT NULL DEFAULT '0' COMMENT '0x65',
  `bar2` bigint(255) NOT NULL DEFAULT '0' COMMENT '0x66',
  `bar3` bigint(255) NOT NULL DEFAULT '0' COMMENT '0x67',
  `bar4` bigint(255) NOT NULL DEFAULT '0' COMMENT '0x68',
  `bar5` bigint(255) NOT NULL DEFAULT '0' COMMENT '0x69',
  `bar6` bigint(255) NOT NULL DEFAULT '0' COMMENT '0x6a',
  `bar7` bigint(255) NOT NULL DEFAULT '0' COMMENT '0x6b',
  `bar8` bigint(255) NOT NULL DEFAULT '0' COMMENT '0x6c',
  `bar9` bigint(255) NOT NULL DEFAULT '0' COMMENT '0x6d',
  PRIMARY KEY (`id`)
) ENGINE=MyISAM DEFAULT CHARSET=latin1;

--
-- Daten für Tabelle `player_bar`
--

INSERT INTO `player_bar` (`id`, `bar0`, `bar1`, `bar2`, `bar3`, `bar4`, `bar5`, `bar6`, `bar7`, `bar8`, `bar9`) VALUES(2020, 769094, 769099, 769099, 0, 0, 0, 0, 0, 0, 0);
INSERT INTO `player_bar` (`id`, `bar0`, `bar1`, `bar2`, `bar3`, `bar4`, `bar5`, `bar6`, `bar7`, `bar8`, `bar9`) VALUES(2021, 769094, 769099, 0, 0, 0, 0, 0, 0, 0, 0);

-- --------------------------------------------------------

--
-- Tabellenstruktur für Tabelle `player_items`
--

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

-- --------------------------------------------------------

--
-- Tabellenstruktur für Tabelle `p_abilities`
--

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

--
-- Daten für Tabelle `p_abilities`
--

INSERT INTO `p_abilities` (`name`, `id`, `slot`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Rest', 269272, 106, 1312109365, 2023574725, 3133773807, 3870783406, 1887832719);
INSERT INTO `p_abilities` (`name`, `id`, `slot`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Normal Attack Left', 769094, 108, 1263352118, 3236668225, 2153842419, 314062142, 3015939650);
INSERT INTO `p_abilities` (`name`, `id`, `slot`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Normal Attack right', 769095, 109, 1346650673, 1688691897, 2528496312, 3832970358, 2802705639);
INSERT INTO `p_abilities` (`name`, `id`, `slot`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Normal Attack Front', 769099, 112, 1227967307, 3443694845, 1660108369, 1296666148, 2957995211);
INSERT INTO `p_abilities` (`name`, `id`, `slot`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('toggle combat', 3316611, 1250, 1280331096, 2235756528, 749634890, 3339552459, 3958402763);
INSERT INTO `p_abilities` (`name`, `id`, `slot`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('switch weapon', 3675584, 259, 1278563891, 1677823321, 1538326685, 1383384407, 1833387476);

-- --------------------------------------------------------

--
-- Tabellenstruktur für Tabelle `p_items`
--

CREATE TABLE IF NOT EXISTS `p_items` (
  `name` text,
  `id` int(11) NOT NULL DEFAULT '0',
  `level` tinyint(11) DEFAULT NULL,
  `data0` bigint(12) NOT NULL DEFAULT '0',
  `data1` bigint(12) DEFAULT NULL,
  `data2` bigint(12) DEFAULT NULL,
  `data3` bigint(12) DEFAULT NULL,
  `data4` bigint(12) DEFAULT NULL,
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=latin1;

--
-- Daten für Tabelle `p_items`
--

INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Combatant''s Hauberk', 13826, 1, 0, 1761429601, 4267309034, 1871298002, 3660826526);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Broken Oar', 23246, 1, 0, 2334750947, 2924708619, 224099686, 696623358);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Note to Redrik', 30103, 1, 0, 600825310, 3035087016, 590233302, 4129698369);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Crate of Steel', 31055, 1, 0, 3397000709, 4114443388, 2474272004, 543818816);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Key to Treasure Chest', 106593, 1, 0, 2628984201, 1939133198, 3232602062, 2230459625);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Pict Bow', 107008, 1, 0, 1348576890, 3541144249, 3373375990, 2684477271);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Pictish Hatchet', 107060, 1, 0, 296542805, 877505375, 261465238, 3679981017);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Scavenger Lackey wrist', 116743, 1, 1297101135, 1392704094, 768313050, 508199535, 2737519313);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('The Phoenix Medallion', 143730, 1, 0, 2243778107, 4049139407, 3982730865, 2238571253);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Mulched Millet', 163108, 1, 0, 752404529, 264395362, 2780565941, 3293026163);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Tangy Yogurt', 163111, 1, 0, 1970171023, 645699337, 3491290020, 2549464430);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Baked Perch', 163116, 1, 0, 460421246, 2424086957, 1972126995, 3529567293);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Boiled Pulped Yams', 163135, 5, 0, 1959869464, 3799578848, 1139599815, 2394585960);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Aged Bloodbelly Ale', 180481, 1, 0, 53815114, 3461317735, 3278618463, 3965282287);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Scavenger Lackey Boots', 189896, 1, 1496927029, 2515629692, 223666041, 2074854397, 2685483642);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Kalanthes_legs', 189901, 1, 1211258962, 2770047622, 3269964241, 3397067491, 52707215);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Scavenger Lackey Chest', 189905, 1, 1446269011, 540283873, 209686719, 1332375380, 3472457292);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Kalanthes_wrist', 190410, 1, 1160988489, 220933799, 3989172023, 1072201772, 17982806);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Kalanther_Shoulder', 190411, 1, 894061657, 4232037883, 3471634776, 719281480, 3984026815);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Acheronian Gate Key', 193186, 1, 0, 3248741215, 3615979884, 3971608201, 1719127907);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Scavenger Lackey Pants', 193680, 1, 826618178, 2224710269, 254678546, 2295899962, 730084146);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Scavenger Lackey Belt', 194918, 1, 909328966, 2627116819, 1153130979, 1047930855, 547216752);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Scavenger Lackey Main Hand Weapon', 195325, 5, 1496599367, 2083615939, 506416654, 2891453105, 2095429198);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Broken Bottle', 215711, 1, 1330596153, 224759543, 1426570559, 9292436, 3903475121);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Personal Letter', 222055, 1, 0, 3525588992, 1162661755, 2339135584, 1117013710);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Yellow Rags', 223250, 1, 0, 1820086162, 1587540222, 3304642498, 1128830123);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Leather Strap', 225558, 1, 0, 3377619978, 2274605389, 2534888369, 131645687);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Half-empty Jack of Ale', 233632, 1, 0, 326265756, 1893477989, 2602683954, 1297198706);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Sparkling Blue Stone', 234290, 1, 0, 2576662302, 2875295398, 2096652719, 2935942142);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Scavenger''s Key', 319224, 1, 0, 2785307472, 227917107, 2973513335, 1760697862);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Blue Gown', 324066, 1, 1295603513, 2186481383, 298075711, 631971044, 3517685871);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Blue Bottom', 324176, 1, 1330660145, 58376958, 1625310911, 2224176424, 3134255698);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Ape King''s Treasure', 354477, 1, 0, 2793834368, 274664068, 2351730676, 1281783167);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Faded Blue Bandana', 873939, 1, 0, 2941447629, 25982365, 1153237650, 2630327467);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Seadog''s Vest', 873940, 1, 0, 787751847, 2760805048, 3216134507, 4084030833);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Pirate''s Garish Top', 873953, 1, 0, 3904777005, 35389084, 1314303231, 785760376);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Savage''s Tattered Top', 873955, 1, 0, 1875527510, 4062921988, 3544103116, 838663775);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Seadog''s Shorts', 873961, 1, 0, 656382355, 1322000129, 3599302866, 222101924);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Savage''s Tattered Loincloth', 873965, 1, 0, 1504478861, 3940622585, 2762030037, 3188710054);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Pirate''s Greasy Belt', 873968, 1, 0, 652465958, 1371567493, 369318126, 3891108565);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Seadog''s Boots', 873976, 1, 0, 525549966, 4109343803, 1687766390, 2907510573);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Savage''s Footgear', 873977, 1, 0, 3760512089, 697167813, 91820709, 3457972641);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Jungle Boots', 873978, 1, 0, 567178156, 2482664852, 1726605055, 3900310206);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Pictish Featherskull', 3104642, 1, 0, 2164683436, 3956406582, 2231652016, 2334474559);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Bloodied Pictish Hatchet', 3113320, 1, 0, 3922246191, 584236327, 2815961765, 3379492390);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Lich Necklace', 3113357, 1, 0, 172124859, 4052537100, 2298211660, 3481105295);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Scourgewater', 3124785, 1, 0, 2052868851, 2387254317, 1933217027, 817102616);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Skirmisher''s Coif', 3147888, 1, 0, 2012187436, 818722303, 3943642773, 204074148);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Saddur''s Scimitar', 3203905, 5, 0, 3850798779, 2872848163, 2797342250, 2721365187);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Ehtuu-Agh''s Dirk', 3236389, 5, 0, 2729550681, 3408214652, 1081327382, 2702129185);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Poacher''s Gaff', 3237313, 5, 0, 4272634277, 1150582889, 1950084807, 1281767632);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Slave Manacles', 3361557, 1, 1129664565, 3714105241, 1980330998, 2034888138, 2381170642);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Acolyte''s Staff', 3394679, 5, 0, 1194720956, 3702592793, 3738645590, 4207291681);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Liberator''s Greatsword', 3419673, 2, 0, 4026237629, 421725451, 2360888711, 1523151928);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Delia''s Ring', 3419931, 1, 0, 1764713738, 3682980775, 220921603, 1931462779);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Tavern Slippers', 3431663, 5, 0, 2797244740, 2811990232, 3244936175, 100787858);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Gold Tooth', 3478921, 1, 0, 1299639773, 1719476330, 1091424400, 1346587040);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Pict Nose-bone', 3478955, 1, 0, 3176677165, 2811252570, 225265852, 2299282264);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Banana', 3478961, 2, 0, 353713122, 3847871819, 2896696701, 3370585746);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Vial of Demon Blood', 3478965, 1, 0, 3106175141, 1150487683, 2479905984, 2380406794);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Fresh Bloodbelly Ale', 3478994, 1, 1128673592, 3996932914, 1018457361, 3313197998, 1697007879);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Pictish Zaghnal', 3512292, 1, 0, 2027375113, 1708152871, 3200039701, 3550419402);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Pictish Tunic', 3512598, 1, 0, 2988991063, 31797309, 3753714404, 3380810360);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Totem of Origins', 3514741, 5, 0, 3714131783, 1767174244, 4246798473, 66060749);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('King Conan''s Tribute', 3657882, 6, 0, 1574204534, 3146544601, 3042924383, 3108909465);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Seadog''s Belt', 3666253, 1, 0, 3494074669, 2986262881, 2672172131, 2950671482);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Machete', 3694465, 2, 0, 3462823447, 959478366, 4111960830, 3779198312);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Cudgel', 3694503, 1, 0, 3767769345, 1427518743, 1916888652, 1201470463);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Falchion', 3694922, 1, 0, 4205569792, 1749503540, 1654012294, 1146679963);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Iron Greatsword', 3695212, 2, 0, 2631397617, 368197172, 2221578147, 2940999746);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Warclub', 3695258, 2, 0, 397554976, 3232274232, 3101963107, 2708762672);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Sledgehammer', 3695390, 1, 0, 89914222, 1894633297, 264340897, 1107181597);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Heavy Club', 3695945, 5, 0, 2209657130, 2937135675, 1501202300, 3330691073);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Bronze Claymore', 3696233, 5, 0, 260212570, 3128793677, 1678146443, 86759805);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Iron Great Scimitar', 3696472, 1, 0, 3177624064, 2651891526, 1838020168, 386507873);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Iron Cutlass', 3696514, 5, 0, 3097493836, 3746778926, 2328571825, 4225410916);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Hammer', 3696863, 2, 0, 4021784163, 2057074900, 3857739332, 3282481534);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Rawhide Belt', 3696997, 5, 0, 3267510848, 119112252, 2168416004, 1263448638);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Padded-leather Bracers', 3697400, 2, 0, 3914971628, 778894153, 1153259576, 1797597544);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Frayed Bracers', 3697502, 5, 0, 2661486256, 1830528900, 2918891735, 3638094097);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Coarse Robe', 3697829, 1, 0, 658636751, 2048703339, 1437816398, 151915877);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Rough-stitched Tunic', 3697861, 2, 0, 1523372737, 4158082777, 2708593146, 885902849);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Frenzied Rawhide Armbands', 3697936, 1, 0, 842305109, 2699872980, 792974511, 3136933872);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Rawhide Boots', 3698179, 1, 0, 1247425097, 3635903006, 1637981683, 2790783766);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Cured Cap', 3698208, 2, 0, 2260022400, 3090298046, 4080198206, 304571288);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Padded-leather Tunic', 3698460, 2, 0, 3496102081, 1814031675, 3916102537, 2636433747);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Cured Fauld', 3698580, 2, 0, 3630028945, 509085201, 455281536, 2305724670);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Frayed Hood', 3698766, 5, 0, 3995765686, 3507096764, 2710599555, 2406638520);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Frayed Circlet', 3698809, 5, 0, 3430185055, 4238330612, 2209985514, 1009737997);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Padded-leather Boots', 3698901, 2, 0, 1190121656, 2915502051, 1538730501, 2095616191);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Rawhide Cap', 3698969, 5, 0, 440259889, 514228140, 1113916326, 2428934234);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Coarse Pants', 3699057, 1, 0, 2679110150, 3192504378, 2420887177, 2735200863);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Padded-leather Gloves', 3699194, 1, 0, 4035176014, 2704617453, 2375620062, 2020147762);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Rawhide Gauntlets', 3699378, 1, 0, 431745164, 872143686, 1872825962, 1733693490);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Frayed Gloves', 3700117, 5, 0, 1251267468, 2025664182, 71820620, 1296984849);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Frayed Leggings', 3700883, 5, 0, 1536989224, 604626724, 1619350291, 2332242720);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Cured Wrist-straps', 3701133, 2, 0, 3591896946, 1749395120, 116500709, 785018113);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Cured Shoes', 3701240, 2, 0, 1277841380, 897968448, 2864778223, 1454651809);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Rough-stitched Robe', 3701382, 2, 0, 525171456, 2582783324, 3071443605, 124257450);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Coarse Sash', 3702225, 1, 0, 473455424, 400308560, 1421289244, 847640743);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Padded-leather Armbands', 3702448, 2, 0, 2254680234, 1830256425, 527300628, 2460721293);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Rawhide Armbands', 3702675, 1, 0, 2863633508, 2138404829, 2424556998, 2795025525);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Rawhide Helm', 3702700, 1, 0, 2847459475, 3216649975, 2785739468, 1812030223);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Rough-stitched Arm-straps', 3702808, 2, 0, 2206194397, 3046440267, 628808585, 3977354217);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Padded-leather Shoes', 3703099, 1, 0, 770802125, 186869218, 3930933, 936396730);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Padded-leather Tasset', 3703460, 2, 0, 1081955057, 4054523849, 103581816, 1787880431);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Padded-leather Girdle', 3703468, 2, 0, 999347361, 1668489136, 3624199848, 299157503);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Rawhide Jerkin', 3703574, 5, 0, 2505478822, 1182393578, 393945033, 3801213292);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Frayed Tunic', 3704384, 5, 0, 2634636142, 3671381562, 1580722702, 3878240861);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Coarse Tunic', 3704401, 1, 0, 275357952, 3625559693, 2869052403, 1256830182);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Coarse Arm-straps', 3704404, 1, 0, 29853986, 107825550, 2692274008, 2482520620);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Cured Belt', 3704470, 2, 0, 2416777967, 465001116, 2452654336, 420497419);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Rough-stitched Slippers', 3704688, 2, 0, 1989802370, 936452530, 2544684369, 1387372666);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Frayed Sash', 3704982, 5, 0, 2398658089, 2355952451, 4285932997, 2112270607);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Padded-leather Wristbands', 3705244, 1, 0, 2443697356, 3764490937, 2822994047, 536187869);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Vitalic Padded-leather Fauld', 3705327, 1, 0, 1420476688, 4107199323, 3621291150, 3126522234);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Frayed Mitts', 3705731, 5, 0, 1490634191, 1053618781, 3340908122, 2188060038);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Frayed Arm-straps', 3705737, 5, 0, 4126594243, 1417215138, 4127224592, 3909101236);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Padded-leather Cap', 3706225, 1, 0, 3455788000, 1117417633, 3124679419, 2530626784);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Padded-leather Gauntlets', 3707367, 2, 0, 210795969, 4172821417, 2143867773, 3414779444);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Padded-leather Helm', 3707381, 2, 0, 2425999993, 3017134285, 2752787528, 1933932268);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Rawhide Bracers', 3708149, 1, 0, 4292825761, 1599251194, 301326810, 2951985516);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Rawhide Shoes', 3708344, 5, 0, 97259538, 3503485124, 3942335516, 1931689853);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Rawhide Girdle', 3708357, 1, 0, 3976716384, 2738859016, 3260345992, 555849637);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Rawhide Gloves', 3708951, 5, 0, 2303568762, 1495793784, 1107837549, 2187532118);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Cured Gloves', 3709555, 2, 0, 2443063569, 2091844568, 86413802, 493955879);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Coarse Padsoles', 3711348, 1, 0, 2526300525, 1418068240, 3410863812, 1541280404);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Mighty Padded-leather Gloves', 3712100, 1, 0, 3093764875, 2466329417, 2318970586, 445619654);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Padded-leather Fauld', 3712326, 1, 0, 7438720, 2365992746, 2716165097, 2385284602);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Rawhide Tunic', 3712387, 1, 0, 686294493, 3370100029, 2500798193, 3770088659);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Rawhide Tasset', 3712565, 1, 0, 1482591975, 2171830429, 2770082027, 353272306);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Rough-stitched Leggings', 3712676, 2, 0, 70823300, 2713000564, 2245495864, 3462256353);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Rawhide Wrist-straps', 3713069, 5, 0, 367312236, 203512856, 482467894, 286622301);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Rawhide Fauld', 3713841, 5, 0, 3970969756, 1396761940, 2883290340, 239140193);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Padded-leather Belt', 3714090, 1, 0, 3144212656, 3819605471, 1362476403, 3793220080);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Padded-leather Jerkin', 3714456, 1, 0, 3396345762, 643365881, 3556010018, 2625127228);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Scramasax', 3714763, 1, 0, 1005653597, 2712294996, 3394296302, 3871524044);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Frayed Robe', 3715039, 5, 0, 1839553214, 1714688032, 160235776, 3673864028);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Coarse Wristbands', 3715095, 1, 0, 1454963421, 110371691, 3840768832, 2027915499);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Coarse Circlet', 3715969, 1, 0, 3762997372, 4213380035, 2523742481, 1212239074);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Frayed Wristbands', 3716281, 5, 0, 2618041736, 395683018, 1714139299, 36480797);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Coarse Bracers', 3716388, 1, 0, 3551892765, 265144419, 85073185, 2785847816);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Rough-stitched Belt', 3716418, 2, 0, 2217324556, 3385875658, 3128972266, 2763457445);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Cured Jerkin', 3716538, 2, 0, 1845074817, 2989664793, 2398301722, 1479557382);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Coarse Armbands', 3717058, 1, 0, 2769482170, 2463783396, 2869166550, 2740304353);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Coarse Leggings', 3717681, 1, 0, 56313725, 2858301196, 3086536678, 2813370476);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Stiletto', 3718170, 2, 0, 2249945352, 3374017262, 284371821, 3888190522);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Razor', 3718489, 5, 0, 4124617839, 1683405507, 2849818941, 190619465);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Pitchfork', 3718779, 5, 0, 2114344093, 3247719341, 3487105374, 791447596);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Wooden Stave', 3719472, 5, 0, 1165663340, 3683261520, 681099883, 567539263);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Skewed Bow', 3719698, 5, 0, 1034904562, 3126996015, 3641000955, 1961934792);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Rough-stitched Belt', 3720143, 2, 0, 2967837512, 2787024922, 3082539374, 1049570442);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Coarse Hood', 3720186, 1, 0, 2521499726, 1305238147, 3227394073, 320480557);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Frayed Padsoles', 3720254, 5, 0, 217870774, 2328976527, 2507050631, 278743790);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Short Bow', 3720287, 2, 0, 1671459469, 2324212425, 3346527787, 3572290446);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Rough-stitched Circlet', 3720425, 2, 0, 75066500, 308880662, 595917301, 2349776425);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Coarse Slippers', 3720485, 1, 0, 1869409167, 1501646527, 2128480993, 1518776150);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Deflex Bow', 3720606, 1, 0, 1199753409, 1208662904, 422856602, 1085328099);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Rough-stitched Padsoles', 3720618, 2, 0, 1621394792, 39546339, 1402875270, 672037223);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Algid Frayed Gloves', 3720722, 7, 0, 2282498236, 1336746157, 2313754431, 1713468196);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Boar Spear', 3720747, 1, 0, 2325464283, 2304000608, 3127876177, 1255777268);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Billhook', 3720930, 2, 0, 971589549, 2267232040, 648279961, 3627631918);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Rough-stitched Armbands', 3721032, 2, 0, 2208867989, 2736693897, 626752762, 1601360645);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Frayed Slippers', 3721201, 5, 0, 3744269644, 1848255961, 1094632461, 354131301);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Deflex Crossbow', 3721782, 1, 0, 2476745680, 3728601267, 240961388, 2168509750);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Simple Crossbow', 3721837, 5, 0, 2365032911, 663227102, 1549534809, 3976610000);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Light Crossbow', 3721858, 2, 0, 934347306, 1717185730, 894760563, 1878216915);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Bronze Javelin', 3723837, 2, 0, 4212814461, 144049063, 425419088, 3831605173);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Bronze Throwing Axe', 3723894, 1, 0, 3495982991, 974429398, 4094350401, 2055373494);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Bronze Dart', 3723895, 5, 0, 4248016411, 3600637030, 2547764450, 3746337362);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Wooden Targe', 3724017, 2, 0, 3965439802, 2325555535, 3370489220, 3676186958);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Metal Buckler', 3724072, 1, 0, 2217898605, 1901151739, 1495113940, 3321198512);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Wooden Buckler', 3724118, 5, 0, 1691738014, 178454008, 779468218, 4197891995);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Rough-stitched Wristbands', 3724395, 2, 0, 2483273140, 544225911, 3260585591, 504649962);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Coarse Gloves', 3724539, 1, 0, 2426696588, 2247083543, 1083931940, 1287384193);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Coarse Belt', 3724693, 1, 0, 729633681, 1007162099, 4028324724, 3351729806);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Rough-stitched Pants', 3725356, 2, 0, 3573560184, 3844645193, 170370161, 1989665037);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Frayed Pants', 3725575, 5, 0, 3577318676, 4187245701, 2836636070, 3594424784);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Rough-stitched Bracers', 3725754, 2, 0, 3699696491, 1979661027, 2864046153, 2964346507);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Frayed Armbands', 3725857, 5, 0, 2689042333, 1175112205, 998394074, 3656022695);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Coarse Mitts', 3726088, 1, 0, 4284715230, 607819517, 607809877, 4144354736);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Rough-stitched Mitts', 3726137, 2, 0, 1484139748, 4146092466, 705222442, 2640071321);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Frayed Belt', 3726142, 5, 0, 3587712292, 2607450679, 2334864069, 3531236750);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Rough-stitched Hood', 3726152, 2, 0, 2304587822, 3801320657, 2977070935, 903650704);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Rough-stitched Gloves', 3726161, 2, 0, 4069154641, 1975106104, 3494373440, 3111369799);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Fha''quth''s Claw', 3728388, 5, 0, 244148573, 938659341, 1512406042, 833330386);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Medium Bolts', 3735836, 2, 0, 3880736436, 618523338, 1515057466, 918458841);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Medium Arrows', 3736546, 2, 0, 975872934, 2348932398, 3308145654, 3110110643);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Light Arrows', 3736695, 1, 0, 2260572741, 3010751455, 2226019340, 870839295);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Crude Arrows', 3736702, 5, 0, 2342808832, 562932144, 1556245642, 940064315);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Crude Bolts', 3736713, 5, 0, 2286901649, 4241776451, 2438756306, 773184393);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Light Bolts', 3736731, 1, 0, 521380641, 308979084, 2365874819, 3598596832);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Heavy Jungle Club', 3736814, 5, 0, 1787551059, 2116385960, 559882688, 193901891);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Pictish Bow', 3736815, 5, 0, 409808133, 614337629, 3074231733, 1869281776);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Crude Arrows', 3736816, 5, 0, 232130771, 749736885, 419328936, 2565833937);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Pirate Ring', 3737052, 5, 810244171, 2352061302, 909733896, 393700663, 3859773596);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Voidseeker Cowl', 3751148, 8, 0, 2438040746, 3696417965, 2027555974, 3209674183);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Voidseeker Armbands', 3751443, 8, 0, 3136750367, 3789749801, 468782282, 2138346602);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Voidseeker Wristbands', 3751477, 8, 0, 1948782893, 34910460, 2588296479, 295535039);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Padded-leather Brassarts', 3753217, 1, 0, 223977724, 3875314204, 82120638, 2972158670);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Cured Brassarts', 3753349, 2, 0, 1237011538, 4231254603, 2676273021, 2771322870);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Rawhide Brassarts', 3753504, 5, 0, 995622580, 3335790899, 2287572126, 1620522430);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Belt of Dark Terror', 3762958, 8, 0, 3688179342, 3500314334, 274433219, 104303005);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Bindings of Dark Terror', 3762969, 8, 0, 891897366, 3500387266, 1267831159, 2347216609);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Talons of Dark Terror', 3763061, 8, 0, 1135285193, 499140877, 3455434291, 2810784090);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Coloring the Sky', 3765547, 1, 0, 2544224148, 1313320141, 712788339, 1666333955);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Blue Pearl', 3770803, 1, 0, 3918441977, 1860989899, 893854397, 4041172576);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Royal Signet', 3774542, 7, 0, 2515285418, 2815223308, 1368962356, 3023132244);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Necklace of Tireless Sagacity', 3778665, 8, 0, 3006037659, 2547135550, 3015403676, 2549130333);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Nihilistic Pants', 3800356, 8, 0, 3180144961, 501303309, 1588799823, 2819076140);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Obliteratus', 3803265, 8, 0, 2821254921, 2483872870, 3725859743, 1668169315);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Witchwalkers', 3804811, 22, 0, 851453144, 2902492793, 1125085235, 3152602931);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Battlehide Belt', 3804823, 20, 0, 1467964233, 4217108355, 3949471195, 3080965547);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Red Rags', 3805571, 1, 0, 1863480336, 2663400832, 1335090451, 3507225323);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Spiritband', 3805992, 53, 0, 303767656, 3455227115, 2494859082, 977750746);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Nightwhisper Crossbow', 3806206, 8, 0, 1025196950, 1247221033, 4160299309, 3261065452);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Skysplendor Shafts', 3806207, 8, 0, 1801175398, 3790895693, 2620367783, 720179345);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Fha''quth''s Bite', 3850309, 5, 0, 2529152882, 2191749980, 3063568860, 1738820823);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Small Mangled Talon', 3886816, 1, 0, 489937107, 2608410906, 513885308, 2373026206);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Small Chipped Fang', 3886828, 1, 0, 2427435330, 2740583126, 2048017072, 996558536);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Small Mangled Venom Sac', 3887406, 1, 0, 125479708, 207183178, 4072436241, 226550323);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Cloak of Hugin', 3908313, 6, 0, 1061267214, 3206709310, 3057537805, 3890425828);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Bronze Bauble', 3917220, 1, 0, 1175162921, 986391960, 1918682371, 3372426441);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Turach''s Shoes', 4078422, 9, 0, 1231054423, 160614520, 2481066523, 214366163);
INSERT INTO `p_items` (`name`, `id`, `level`, `data0`, `data1`, `data2`, `data3`, `data4`) VALUES('Valerius''s Tenor', 4082388, 1, 0, 4192182819, 2605070291, 154476315, 2989018251);

-- --------------------------------------------------------

--
-- Tabellenstruktur für Tabelle `races`
--

CREATE TABLE IF NOT EXISTS `races` (
  `race_id` tinyint(3) unsigned NOT NULL,
  `race_name` varchar(25) COLLATE latin1_general_ci NOT NULL,
  `race_desc` text COLLATE latin1_general_ci,
  PRIMARY KEY (`race_id`)
) ENGINE=MyISAM DEFAULT CHARSET=latin1 COLLATE=latin1_general_ci;

--
-- Daten für Tabelle `races`
--

INSERT INTO `races` (`race_id`, `race_name`, `race_desc`) VALUES(1, 'Aquilonian', NULL);
INSERT INTO `races` (`race_id`, `race_name`, `race_desc`) VALUES(2, 'Cimmerian', NULL);
INSERT INTO `races` (`race_id`, `race_name`, `race_desc`) VALUES(3, 'Stygian', NULL);

-- --------------------------------------------------------

--
-- Tabellenstruktur für Tabelle `race_arche_class`
--

CREATE TABLE IF NOT EXISTS `race_arche_class` (
  `race_id` tinyint(3) unsigned NOT NULL,
  `arche_id` tinyint(3) unsigned NOT NULL,
  `class_id` tinyint(3) unsigned NOT NULL,
  PRIMARY KEY (`race_id`,`arche_id`,`class_id`),
  KEY `fk_RAC_arche` (`arche_id`),
  KEY `fk_RAC_class` (`class_id`)
) ENGINE=MyISAM DEFAULT CHARSET=latin1 COLLATE=latin1_general_ci;

--
-- Daten für Tabelle `race_arche_class`
--

INSERT INTO `race_arche_class` (`race_id`, `arche_id`, `class_id`) VALUES(1, 1, 1);
INSERT INTO `race_arche_class` (`race_id`, `arche_id`, `class_id`) VALUES(1, 2, 4);
INSERT INTO `race_arche_class` (`race_id`, `arche_id`, `class_id`) VALUES(1, 2, 5);
INSERT INTO `race_arche_class` (`race_id`, `arche_id`, `class_id`) VALUES(1, 2, 6);
INSERT INTO `race_arche_class` (`race_id`, `arche_id`, `class_id`) VALUES(1, 3, 7);
INSERT INTO `race_arche_class` (`race_id`, `arche_id`, `class_id`) VALUES(1, 3, 8);
INSERT INTO `race_arche_class` (`race_id`, `arche_id`, `class_id`) VALUES(1, 3, 9);
INSERT INTO `race_arche_class` (`race_id`, `arche_id`, `class_id`) VALUES(2, 1, 3);
INSERT INTO `race_arche_class` (`race_id`, `arche_id`, `class_id`) VALUES(2, 2, 4);
INSERT INTO `race_arche_class` (`race_id`, `arche_id`, `class_id`) VALUES(2, 2, 5);
INSERT INTO `race_arche_class` (`race_id`, `arche_id`, `class_id`) VALUES(2, 2, 6);
INSERT INTO `race_arche_class` (`race_id`, `arche_id`, `class_id`) VALUES(2, 3, 7);
INSERT INTO `race_arche_class` (`race_id`, `arche_id`, `class_id`) VALUES(2, 3, 8);
INSERT INTO `race_arche_class` (`race_id`, `arche_id`, `class_id`) VALUES(3, 1, 2);
INSERT INTO `race_arche_class` (`race_id`, `arche_id`, `class_id`) VALUES(3, 3, 8);
INSERT INTO `race_arche_class` (`race_id`, `arche_id`, `class_id`) VALUES(3, 3, 9);
INSERT INTO `race_arche_class` (`race_id`, `arche_id`, `class_id`) VALUES(3, 4, 10);
INSERT INTO `race_arche_class` (`race_id`, `arche_id`, `class_id`) VALUES(3, 4, 11);
INSERT INTO `race_arche_class` (`race_id`, `arche_id`, `class_id`) VALUES(3, 4, 12);

-- --------------------------------------------------------

--
-- Tabellenstruktur für Tabelle `realms`
--

CREATE TABLE IF NOT EXISTS `realms` (
  `id` int(11) NOT NULL DEFAULT '0',
  `name` varchar(255) NOT NULL DEFAULT 'Faolan Realm',
  `onlineStatus` int(11) NOT NULL DEFAULT '0',
  `lastUpd` bigint(255) NOT NULL DEFAULT '0',
  `type` int(11) NOT NULL DEFAULT '256' COMMENT '0,1 = PvE | 256,257 = PvP | 1081 = PvP by faction',
  `realmLoad` int(11) NOT NULL DEFAULT '0' COMMENT '0 = medium load 1 = medium load 2 = heavy load 3 = full',
  `activ` int(11) NOT NULL DEFAULT '0',
  `csAddress` varchar(255) NOT NULL DEFAULT '127.0.0.1',
  `csPort` int(11) NOT NULL DEFAULT '7002',
  `agentAddress` varchar(255) NOT NULL DEFAULT '127.0.0.1',
  `agentPort` int(11) NOT NULL DEFAULT '7003',
  `worldAddress` varchar(255) NOT NULL DEFAULT '127.0.0.1',
  `worldPort` int(11) NOT NULL DEFAULT '7004'
) ENGINE=MyISAM DEFAULT CHARSET=latin1;

--
-- Daten für Tabelle `realms`
--

INSERT INTO `realms` (`id`, `name`, `onlineStatus`, `lastUpd`, `type`, `realmLoad`, `activ`, `csAddress`, `csPort`, `agentAddress`, `agentPort`, `worldAddress`, `worldPort`) VALUES(1, 'Faolan Realm', 2, 0, 256, 0, 1, '127.0.0.1', 7002, '127.0.0.1', 7003, '127.0.0.1', 7004);

-- --------------------------------------------------------

--
-- Tabellenstruktur für Tabelle `spelldata`
--

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

--
-- Daten für Tabelle `spelldata`
--

INSERT INTO `spelldata` (`id`, `level`, `name`, `castTime`, `minDmg`, `maxDmg`, `costMana`, `effectSpell`) VALUES(1148653, 1, 'Fire of Gehenna', 2000, 1500, 1700, 16, '1148647');
INSERT INTO `spelldata` (`id`, `level`, `name`, `castTime`, `minDmg`, `maxDmg`, `costMana`, `effectSpell`) VALUES(1148654, 1, 'Hellfire Stream', 1000, 800, 900, 16, '');
INSERT INTO `spelldata` (`id`, `level`, `name`, `castTime`, `minDmg`, `maxDmg`, `costMana`, `effectSpell`) VALUES(1148655, 1, 'Chained Shockstrike', 0, 1100, 1200, 0, '1148655');
INSERT INTO `spelldata` (`id`, `level`, `name`, `castTime`, `minDmg`, `maxDmg`, `costMana`, `effectSpell`) VALUES(1148656, 1, 'ShockStrike', 2500, 6000, 7300, 16, '1148655');
INSERT INTO `spelldata` (`id`, `level`, `name`, `castTime`, `minDmg`, `maxDmg`, `costMana`, `effectSpell`) VALUES(1148665, 1, 'Shield of Infernal Flames', 1500, 0, 0, 4, '');
INSERT INTO `spelldata` (`id`, `level`, `name`, `castTime`, `minDmg`, `maxDmg`, `costMana`, `effectSpell`) VALUES(1148666, 1, 'Storm Chains', 500, 100, 100, 29, '');
INSERT INTO `spelldata` (`id`, `level`, `name`, `castTime`, `minDmg`, `maxDmg`, `costMana`, `effectSpell`) VALUES(1148668, 1, 'Inferno of Amher', 2500, 9600, 11100, 29, '');
INSERT INTO `spelldata` (`id`, `level`, `name`, `castTime`, `minDmg`, `maxDmg`, `costMana`, `effectSpell`) VALUES(1148671, 1, 'Shocblast', 3000, 6100, 7400, 29, '');
INSERT INTO `spelldata` (`id`, `level`, `name`, `castTime`, `minDmg`, `maxDmg`, `costMana`, `effectSpell`) VALUES(1148672, 1, 'Protection of Set', 0, 0, 0, 0, '');
INSERT INTO `spelldata` (`id`, `level`, `name`, `castTime`, `minDmg`, `maxDmg`, `costMana`, `effectSpell`) VALUES(1148673, 1, 'Protection of Set', 1500, 0, 0, 4, '');
INSERT INTO `spelldata` (`id`, `level`, `name`, `castTime`, `minDmg`, `maxDmg`, `costMana`, `effectSpell`) VALUES(1148651, 1, 'Incerate (5)', 0, 100, 100, 0, '1148651');
INSERT INTO `spelldata` (`id`, `level`, `name`, `castTime`, `minDmg`, `maxDmg`, `costMana`, `effectSpell`) VALUES(1148650, 1, 'Incerate (4)', 0, 100, 100, 0, '1148651');
INSERT INTO `spelldata` (`id`, `level`, `name`, `castTime`, `minDmg`, `maxDmg`, `costMana`, `effectSpell`) VALUES(1148649, 1, 'Incerate (3)', 0, 100, 100, 0, '1148650');
INSERT INTO `spelldata` (`id`, `level`, `name`, `castTime`, `minDmg`, `maxDmg`, `costMana`, `effectSpell`) VALUES(1148648, 1, 'Incerate (2)', 0, 100, 100, 0, '1148649');
INSERT INTO `spelldata` (`id`, `level`, `name`, `castTime`, `minDmg`, `maxDmg`, `costMana`, `effectSpell`) VALUES(1148647, 1, 'Incerate (1)', 0, 0, 0, 0, '1148648');
INSERT INTO `spelldata` (`id`, `level`, `name`, `castTime`, `minDmg`, `maxDmg`, `costMana`, `effectSpell`) VALUES(1148644, 1, 'Black Ashes', 0, 0, 0, 0, '');
INSERT INTO `spelldata` (`id`, `level`, `name`, `castTime`, `minDmg`, `maxDmg`, `costMana`, `effectSpell`) VALUES(1148643, 1, 'Black Ashes', 0, 0, 0, 0, '');
INSERT INTO `spelldata` (`id`, `level`, `name`, `castTime`, `minDmg`, `maxDmg`, `costMana`, `effectSpell`) VALUES(1148640, 1, 'Flames of Hell', 0, 4700, 7700, 0, '');
INSERT INTO `spelldata` (`id`, `level`, `name`, `castTime`, `minDmg`, `maxDmg`, `costMana`, `effectSpell`) VALUES(1148639, 1, 'Gate of Hell', 0, 0, 0, 0, '');
INSERT INTO `spelldata` (`id`, `level`, `name`, `castTime`, `minDmg`, `maxDmg`, `costMana`, `effectSpell`) VALUES(1148638, 1, 'Gate of Hell', 0, 0, 0, 0, '');
INSERT INTO `spelldata` (`id`, `level`, `name`, `castTime`, `minDmg`, `maxDmg`, `costMana`, `effectSpell`) VALUES(1148590, 1, 'Gate of Hell', 1500, 0, 0, 19, '');
INSERT INTO `spelldata` (`id`, `level`, `name`, `castTime`, `minDmg`, `maxDmg`, `costMana`, `effectSpell`) VALUES(3147658, 1, 'unk0', 0, 0, 0, 0, '');

-- --------------------------------------------------------

--
-- Tabellenstruktur für Tabelle `worldobjects`
--

CREATE TABLE IF NOT EXISTS `worldobjects` (
  `typ` bigint(255) NOT NULL DEFAULT '0',
  `id` bigint(255) NOT NULL DEFAULT '0',
  `objId` bigint(255) NOT NULL DEFAULT '0',
  `pos1` bigint(255) NOT NULL DEFAULT '0',
  `pos2` bigint(255) NOT NULL DEFAULT '0',
  `pos3` bigint(255) NOT NULL DEFAULT '0',
  `pos4` bigint(255) NOT NULL DEFAULT '0',
  `pos5` bigint(255) NOT NULL DEFAULT '0',
  `pos6` bigint(255) NOT NULL DEFAULT '0',
  `pos7` bigint(255) NOT NULL DEFAULT '0',
  `map` bigint(255) NOT NULL DEFAULT '0',
  `maxSlots` int(11) NOT NULL DEFAULT '4',
  `sdata1` longtext NOT NULL,
  `sdata2` longtext NOT NULL,
  `items` longtext NOT NULL
) ENGINE=MyISAM DEFAULT CHARSET=latin1;

--
-- Daten für Tabelle `worldobjects`
--

INSERT INTO `worldobjects` (`typ`, `id`, `objId`, `pos1`, `pos2`, `pos3`, `pos4`, `pos5`, `pos6`, `pos7`, `map`, `maxSlots`, `sdata1`, `sdata2`, `items`) VALUES(4036371742, 13404, 3662930, 1133641302, 1124866589, 1143918625, 3183656051, 1053293350, 1015654002, 1063935323, 4010, 10, 'Chest [N7FU]', '<remoteformat id="13782" category="50200" key="!,1%VZc#)*ctk-zNFNxI" knubot="0"  ></remoteformat>', '3478994|100|1-3478994|50|1-3737052|50|1-3770803|50|1');
INSERT INTO `worldobjects` (`typ`, `id`, `objId`, `pos1`, `pos2`, `pos3`, `pos4`, `pos5`, `pos6`, `pos7`, `map`, `maxSlots`, `sdata1`, `sdata2`, `items`) VALUES(4036371742, 5397, 3662930, 1135509111, 1125738742, 1144659329, 3163964968, 3207009483, 1025527326, 3208750203, 4010, 10, 'Chest [N7FU]', '<remoteformat id="13782" category="50200" key="!,1%VZc#)*ctk-zNFNxI" knubot="0"  ></remoteformat>', '3478994|100|1-3770803|100|1-3770803|100|1');
INSERT INTO `worldobjects` (`typ`, `id`, `objId`, `pos1`, `pos2`, `pos3`, `pos4`, `pos5`, `pos6`, `pos7`, `map`, `maxSlots`, `sdata1`, `sdata2`, `items`) VALUES(4036371742, 13405, 3662930, 1134784214, 1125240082, 1144076176, 0, 0, 0, 0, 4010, 10, 'Chest [N7FU]', '<remoteformat id="13782" category="50200" key="!,1%VZc#)*ctk-zNFNxI" knubot="0"  ></remoteformat>', '3478994|100|1-3770803|50|1-3737052|100|1');
INSERT INTO `worldobjects` (`typ`, `id`, `objId`, `pos1`, `pos2`, `pos3`, `pos4`, `pos5`, `pos6`, `pos7`, `map`, `maxSlots`, `sdata1`, `sdata2`, `items`) VALUES(4036371742, 5395, 3662930, 1130972578, 1125416501, 1141737570, 3160654085, 3200963662, 1023664116, 3211452995, 4010, 10, 'Chest [N7FU]', '<remoteformat id="13782" category="50200" key="!,1%VZc#)*ctk-zNFNxI" knubot="0"  ></remoteformat>', '3478994|100|1-3770803|100|1');
INSERT INTO `worldobjects` (`typ`, `id`, `objId`, `pos1`, `pos2`, `pos3`, `pos4`, `pos5`, `pos6`, `pos7`, `map`, `maxSlots`, `sdata1`, `sdata2`, `items`) VALUES(4036371742, 5398, 3662930, 1142370692, 1126843364, 1146699264, 1005231727, 1064047613, 3175278180, 3200547721, 4010, 10, 'Chest [N7FU]', '<remoteformat id="13782" category="50200" key="!,1%VZc#)*ctk-zNFNxI" knubot="0"  ></remoteformat>', '3478994|100|1-3478994|100|1-3770803|100|1-3770803|100|1');
INSERT INTO `worldobjects` (`typ`, `id`, `objId`, `pos1`, `pos2`, `pos3`, `pos4`, `pos5`, `pos6`, `pos7`, `map`, `maxSlots`, `sdata1`, `sdata2`, `items`) VALUES(4036371742, 5399, 3662930, 1142195831, 1127184138, 1147980939, 3166834543, 3167807353, 1010160739, 3212825657, 4010, 10, 'Chest [N7FU]', '<remoteformat id="13782" category="50200" key="!,1%VZc#)*ctk-zNFNxI" knubot="0"  ></remoteformat>', '3478994|100|1-3478994|100|1-3737052|100|1-3770803|100|1');
INSERT INTO `worldobjects` (`typ`, `id`, `objId`, `pos1`, `pos2`, `pos3`, `pos4`, `pos5`, `pos6`, `pos7`, `map`, `maxSlots`, `sdata1`, `sdata2`, `items`) VALUES(4036371742, 5400, 3662930, 1143774937, 1127479247, 1142914580, 3167109421, 1049225412, 1019301503, 1064723198, 4010, 10, 'Chest [N7FU]', '<remoteformat id="13782" category="50200" key="!,1%VZc#)*ctk-zNFNxI" knubot="0"  ></remoteformat>', '3478994|100|1');
INSERT INTO `worldobjects` (`typ`, `id`, `objId`, `pos1`, `pos2`, `pos3`, `pos4`, `pos5`, `pos6`, `pos7`, `map`, `maxSlots`, `sdata1`, `sdata2`, `items`) VALUES(1722735952, 17721, 200556, 1132820759, 1092502338, 1133588316, 0, 1057216904, 0, 1062956876, 4000, 10, '4000_stygian_guard_lookout', '<localized id="837652" category="55000" flags="" key="x6&#92;3&#36;UmAa[D&quot;QTO_KX&#92;&#39;" >', '');
INSERT INTO `worldobjects` (`typ`, `id`, `objId`, `pos1`, `pos2`, `pos3`, `pos4`, `pos5`, `pos6`, `pos7`, `map`, `maxSlots`, `sdata1`, `sdata2`, `items`) VALUES(1722735952, 17722, 133173, 1132657345, 1086372341, 1133604405, 0, 0, 0, 1065353216, 4000, 10, 'SimpleDynel [---- ]', '<localized id="347646" category="55000" flags="" key="zb7Mhp.Dm@*5YCN,(k#I" >', '');
INSERT INTO `worldobjects` (`typ`, `id`, `objId`, `pos1`, `pos2`, `pos3`, `pos4`, `pos5`, `pos6`, `pos7`, `map`, `maxSlots`, `sdata1`, `sdata2`, `items`) VALUES(1722735952, 17723, 133173, 1132654494, 1086411348, 1133808222, 0, 0, 0, 1065353216, 4000, 10, 'SimpleDynel [---- ]', '<localized id="347648" category="55000" flags="" key="ao1VMXdAQg9&#39;,NK2`b*A" >', '');
INSERT INTO `worldobjects` (`typ`, `id`, `objId`, `pos1`, `pos2`, `pos3`, `pos4`, `pos5`, `pos6`, `pos7`, `map`, `maxSlots`, `sdata1`, `sdata2`, `items`) VALUES(1722735952, 17724, 133173, 1132608815, 1087926646, 1133836993, 0, 0, 0, 1065353216, 4000, 10, 'SimpleDynel [---- ]', '<localized id="347650" category="55000" flags="" key="0gd@xOpE_.w8Oa^&#39;[EbX" >', '');
INSERT INTO `worldobjects` (`typ`, `id`, `objId`, `pos1`, `pos2`, `pos3`, `pos4`, `pos5`, `pos6`, `pos7`, `map`, `maxSlots`, `sdata1`, `sdata2`, `items`) VALUES(1722735952, 17725, 133173, 1132612059, 1087974125, 1133625836, 0, 0, 0, 1065353216, 4000, 10, 'SimpleDynel [---- ]', '<localized id="347652" category="55000" flags="" key="cDM/L&amp;*%5aSnVNJwB&quot;bJ" >', '');
INSERT INTO `worldobjects` (`typ`, `id`, `objId`, `pos1`, `pos2`, `pos3`, `pos4`, `pos5`, `pos6`, `pos7`, `map`, `maxSlots`, `sdata1`, `sdata2`, `items`) VALUES(1722735952, 17726, 132028, 1132612420, 1088122813, 1133678133, 0, 0, 0, 1065353216, 4000, 10, 'left_rower', '<localized id="347667" category="55000" flags="" key="_DZx&amp;gMHc&#36;T(^8@k*2O3" >', '');
INSERT INTO `worldobjects` (`typ`, `id`, `objId`, `pos1`, `pos2`, `pos3`, `pos4`, `pos5`, `pos6`, `pos7`, `map`, `maxSlots`, `sdata1`, `sdata2`, `items`) VALUES(1722735952, 17727, 132028, 1132658491, 1086577611, 1133548438, 0, 0, 0, 1065353216, 4000, 10, 'left_rower', '<localized id="347670" category="55000" flags="" key="F_R*&quot;j-+t!Fgb]#Y68K2" >', '');
INSERT INTO `worldobjects` (`typ`, `id`, `objId`, `pos1`, `pos2`, `pos3`, `pos4`, `pos5`, `pos6`, `pos7`, `map`, `maxSlots`, `sdata1`, `sdata2`, `items`) VALUES(1722735952, 17728, 133172, 1132861882, 1088023240, 1134393426, 0, 0, 0, 1065353216, 4000, 10, 'SimpleDynel [---- ]', '<localized id="347653" category="55000" flags="" key="]M[8hVNRr6q&#92;IUUAE9VO" >', '');
INSERT INTO `worldobjects` (`typ`, `id`, `objId`, `pos1`, `pos2`, `pos3`, `pos4`, `pos5`, `pos6`, `pos7`, `map`, `maxSlots`, `sdata1`, `sdata2`, `items`) VALUES(1722735952, 17729, 133172, 1132852937, 1088017075, 1134086226, 0, 0, 0, 1065353216, 4000, 10, 'SimpleDynel [---- ]', '<localized id="347654" category="55000" flags="" key="!rH5kzXkNs,IDg#YQ&amp;Bn" >', '');
INSERT INTO `worldobjects` (`typ`, `id`, `objId`, `pos1`, `pos2`, `pos3`, `pos4`, `pos5`, `pos6`, `pos7`, `map`, `maxSlots`, `sdata1`, `sdata2`, `items`) VALUES(1722735952, 17730, 133172, 1132853985, 1087975425, 1134188528, 0, 0, 0, 1065353216, 4000, 10, 'SimpleDynel [---- ]', '<localized id="347655" category="55000" flags="" key="y1idkekjd%?L&quot;Hnbb&#36;j?" >', '');
INSERT INTO `worldobjects` (`typ`, `id`, `objId`, `pos1`, `pos2`, `pos3`, `pos4`, `pos5`, `pos6`, `pos7`, `map`, `maxSlots`, `sdata1`, `sdata2`, `items`) VALUES(1722735952, 17731, 133172, 1132808733, 1086486636, 1134064665, 0, 0, 0, 1065353216, 4000, 10, 'SimpleDynel [---- ]', '<localized id="347656" category="55000" flags="" key="GwkP_9YudG-o2&#39;&#36;vujJk" >', '');
INSERT INTO `worldobjects` (`typ`, `id`, `objId`, `pos1`, `pos2`, `pos3`, `pos4`, `pos5`, `pos6`, `pos7`, `map`, `maxSlots`, `sdata1`, `sdata2`, `items`) VALUES(1722735952, 17732, 133172, 1132857885, 1087950784, 1134292664, 0, 0, 0, 1065353216, 4000, 10, 'SimpleDynel [---- ]', '<localized id="347657" category="55000" flags="" key="C,.ESC4!vWSADB5YhxPh" >', '');
INSERT INTO `worldobjects` (`typ`, `id`, `objId`, `pos1`, `pos2`, `pos3`, `pos4`, `pos5`, `pos6`, `pos7`, `map`, `maxSlots`, `sdata1`, `sdata2`, `items`) VALUES(1722735952, 17733, 133173, 1132612125, 1088002458, 1134190920, 0, 0, 0, 1065353216, 4000, 10, 'SimpleDynel [---- ]', '<localized id="347659" category="55000" flags="" key="`)Z32)+]W9X!JNi%3IEw" >', '');
INSERT INTO `worldobjects` (`typ`, `id`, `objId`, `pos1`, `pos2`, `pos3`, `pos4`, `pos5`, `pos6`, `pos7`, `map`, `maxSlots`, `sdata1`, `sdata2`, `items`) VALUES(1722735952, 17734, 133173, 1132657115, 1086346735, 1134070137, 0, 0, 0, 1065353216, 4000, 10, 'SimpleDynel [---- ]', '<localized id="347660" category="55000" flags="" key="&#39;q&#39;@1EYCHB,N[^HQ9l*S" >', '');
INSERT INTO `worldobjects` (`typ`, `id`, `objId`, `pos1`, `pos2`, `pos3`, `pos4`, `pos5`, `pos6`, `pos7`, `map`, `maxSlots`, `sdata1`, `sdata2`, `items`) VALUES(1722735952, 17735, 133173, 1132608684, 1088015334, 1134089339, 0, 0, 0, 1065353216, 4000, 10, 'SimpleDynel [---- ]', '<localized id="347661" category="55000" flags="" key="ms/Kf0Xo5)YdgrT`U]q&amp;" >', '');
INSERT INTO `worldobjects` (`typ`, `id`, `objId`, `pos1`, `pos2`, `pos3`, `pos4`, `pos5`, `pos6`, `pos7`, `map`, `maxSlots`, `sdata1`, `sdata2`, `items`) VALUES(1722735952, 17736, 133173, 1132611076, 1088027372, 1133984612, 0, 0, 0, 1065353216, 4000, 10, 'SimpleDynel [---- ]', '<localized id="347662" category="55000" flags="" key="-f[B)RUUl.7?i#yKs]0B" >', '');
INSERT INTO `worldobjects` (`typ`, `id`, `objId`, `pos1`, `pos2`, `pos3`, `pos4`, `pos5`, `pos6`, `pos7`, `map`, `maxSlots`, `sdata1`, `sdata2`, `items`) VALUES(1722735952, 17737, 133173, 1132611240, 1087985701, 1133934510, 0, 0, 0, 1065353216, 4000, 10, 'SimpleDynel [---- ]', '<localized id="347663" category="55000" flags="" key="yVmNX5-Xs8@z+4+VY?0H" >', '');
INSERT INTO `worldobjects` (`typ`, `id`, `objId`, `pos1`, `pos2`, `pos3`, `pos4`, `pos5`, `pos6`, `pos7`, `map`, `maxSlots`, `sdata1`, `sdata2`, `items`) VALUES(1722735952, 17738, 133173, 1132612125, 1088002458, 1134190920, 0, 0, 0, 1065353216, 4000, 10, 'SimpleDynel [---- ]', '<localized id="3072626" category="55000" flags="" key="DX6S56]tylompR+X&#39;Y#-" >', '');
