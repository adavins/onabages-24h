-- Esquema de la base de dades de RadioDJ 1.8.2 (backup del 8/1/2026). Només estructura.

CREATE TABLE `carts` (
  `ID` int(11) NOT NULL AUTO_INCREMENT,
  `name` varchar(255) NOT NULL,
  PRIMARY KEY (`ID`),
  UNIQUE KEY `ID` (`ID`)
) ENGINE=MyISAM AUTO_INCREMENT=2 DEFAULT CHARSET=utf8;

CREATE TABLE `carts_list` (
  `ID` int(11) NOT NULL AUTO_INCREMENT,
  `pID` int(11) NOT NULL,
  `swID` int(11) NOT NULL,
  `swButton` int(11) NOT NULL,
  `color` varchar(100) NOT NULL,
  PRIMARY KEY (`ID`),
  UNIQUE KEY `ID` (`ID`)
) ENGINE=MyISAM DEFAULT CHARSET=utf8;

CREATE TABLE `category` (
  `ID` int(11) NOT NULL AUTO_INCREMENT,
  `name` varchar(100) NOT NULL,
  PRIMARY KEY (`ID`),
  UNIQUE KEY `name` (`name`)
) ENGINE=MyISAM AUTO_INCREMENT=14 DEFAULT CHARSET=utf8;

CREATE TABLE `events` (
  `ID` int(11) NOT NULL AUTO_INCREMENT,
  `type` tinyint(2) NOT NULL,
  `time` varchar(20) NOT NULL,
  `name` varchar(200) NOT NULL,
  `date` date DEFAULT '2002-01-01',
  `day` varchar(30) DEFAULT '&',
  `hours` varchar(100) DEFAULT '&',
  `data` text,
  `enabled` enum('True','False') DEFAULT 'True',
  `catID` int(11) NOT NULL DEFAULT '0',
  PRIMARY KEY (`ID`)
) ENGINE=MyISAM AUTO_INCREMENT=381 DEFAULT CHARSET=utf8;

CREATE TABLE `events_categories` (
  `ID` int(11) NOT NULL AUTO_INCREMENT,
  `name` varchar(255) CHARACTER SET latin1 NOT NULL,
  PRIMARY KEY (`ID`)
) ENGINE=MyISAM AUTO_INCREMENT=23 DEFAULT CHARSET=utf8;

CREATE TABLE `genre` (
  `id` int(11) NOT NULL AUTO_INCREMENT,
  `name` varchar(50) NOT NULL,
  KEY `id` (`id`)
) ENGINE=MyISAM AUTO_INCREMENT=151 DEFAULT CHARSET=utf8;

CREATE TABLE `history` (
  `ID` int(11) NOT NULL AUTO_INCREMENT,
  `date_played` datetime DEFAULT '2002-01-01 00:00:01',
  `song_type` tinyint(2) NOT NULL,
  `id_subcat` int(11) NOT NULL,
  `id_genre` int(11) NOT NULL,
  `duration` double(11,5) NOT NULL,
  `artist` varchar(255) NOT NULL,
  `original_artist` varchar(255) NOT NULL,
  `title` varchar(255) NOT NULL,
  `album` varchar(255) NOT NULL,
  `composer` varchar(255) NOT NULL,
  `year` varchar(4) NOT NULL DEFAULT '1900',
  `track_no` smallint(6) NOT NULL DEFAULT '0',
  `disc_no` smallint(6) NOT NULL DEFAULT '0',
  `publisher` varchar(255) NOT NULL,
  `copyright` varchar(255) NOT NULL,
  `isrc` varchar(255) NOT NULL,
  `listeners` mediumint(9) DEFAULT NULL,
  PRIMARY KEY (`ID`),
  KEY `artist` (`artist`),
  KEY `title` (`title`),
  KEY `date_played` (`date_played`)
) ENGINE=MyISAM AUTO_INCREMENT=256645 DEFAULT CHARSET=utf8;

CREATE TABLE `playlists` (
  `ID` int(11) NOT NULL AUTO_INCREMENT,
  `name` varchar(255) NOT NULL,
  PRIMARY KEY (`ID`),
  UNIQUE KEY `ID` (`ID`),
  UNIQUE KEY `name` (`name`)
) ENGINE=MyISAM AUTO_INCREMENT=2 DEFAULT CHARSET=utf8;

CREATE TABLE `playlists_list` (
  `ID` int(11) NOT NULL AUTO_INCREMENT,
  `pID` int(11) NOT NULL,
  `sID` int(11) NOT NULL,
  `cstart` double(11,5) NOT NULL,
  `cnext` double(11,5) NOT NULL,
  `cend` double(11,5) NOT NULL,
  `fin` double(11,5) NOT NULL,
  `fout` double(11,5) NOT NULL,
  `swID` int(11) NOT NULL,
  `swplay` double(11,5) NOT NULL,
  `vtID` int(11) NOT NULL,
  `vtplay` double(11,5) NOT NULL,
  `swfirst` enum('True','False') NOT NULL,
  `ord` int(11) NOT NULL,
  PRIMARY KEY (`ID`),
  KEY `ID` (`ID`)
) ENGINE=MyISAM AUTO_INCREMENT=7 DEFAULT CHARSET=utf8;

CREATE TABLE `queuelist` (
  `ID` int(11) NOT NULL AUTO_INCREMENT,
  `songID` int(11) NOT NULL DEFAULT '0',
  `artist` varchar(250) NOT NULL,
  `swID` int(11) NOT NULL DEFAULT '-1',
  `swPlay` double(11,5) NOT NULL DEFAULT '0.00000',
  `vtID` int(11) NOT NULL DEFAULT '-1',
  `vtPlay` double(11,5) NOT NULL DEFAULT '0.00000',
  PRIMARY KEY (`ID`)
) ENGINE=MyISAM AUTO_INCREMENT=23 DEFAULT CHARSET=utf8 COMMENT='12';

CREATE TABLE `requests` (
  `ID` int(11) NOT NULL AUTO_INCREMENT,
  `songID` int(11) NOT NULL,
  `username` varchar(255) NOT NULL DEFAULT 'Anomymous',
  `userIP` varchar(50) NOT NULL,
  `message` text,
  `requested` datetime NOT NULL,
  `played` int(1) NOT NULL DEFAULT '0',
  PRIMARY KEY (`ID`)
) ENGINE=MyISAM DEFAULT CHARSET=utf8;

CREATE TABLE `rotations` (
  `ID` int(11) NOT NULL AUTO_INCREMENT,
  `name` varchar(255) NOT NULL,
  PRIMARY KEY (`ID`),
  UNIQUE KEY `name` (`name`)
) ENGINE=MyISAM AUTO_INCREMENT=14 DEFAULT CHARSET=utf8;

CREATE TABLE `rotations_list` (
  `ID` int(11) NOT NULL AUTO_INCREMENT,
  `pID` int(11) NOT NULL,
  `catID` int(11) NOT NULL,
  `subID` int(11) NOT NULL,
  `genID` int(11) NOT NULL,
  `selType` int(1) NOT NULL,
  `sweeper` int(1) NOT NULL,
  `repeatRule` set('True','False') NOT NULL,
  `ord` int(2) NOT NULL,
  `data` text NOT NULL,
  PRIMARY KEY (`ID`)
) ENGINE=MyISAM AUTO_INCREMENT=1693 DEFAULT CHARSET=utf8;

CREATE TABLE `songs` (
  `ID` int(11) NOT NULL AUTO_INCREMENT,
  `path` varchar(255) NOT NULL,
  `enabled` int(1) NOT NULL DEFAULT '0',
  `date_played` datetime DEFAULT '2002-01-01 00:00:01',
  `artist_played` datetime DEFAULT '2002-01-01 00:00:01',
  `count_played` mediumint(9) NOT NULL DEFAULT '0',
  `play_limit` int(11) NOT NULL DEFAULT '0',
  `limit_action` int(1) NOT NULL DEFAULT '0',
  `start_date` datetime DEFAULT '2002-01-01 00:00:01',
  `end_date` datetime DEFAULT '2002-01-01 00:00:01',
  `song_type` tinyint(2) NOT NULL,
  `id_subcat` int(11) NOT NULL,
  `id_genre` int(11) NOT NULL,
  `weight` double(5,1) NOT NULL DEFAULT '50.0',
  `duration` double(11,5) NOT NULL,
  `cue_times` varchar(255) NOT NULL DEFAULT '&',
  `precise_cue` tinyint(1) NOT NULL DEFAULT '0',
  `fade_type` tinyint(1) NOT NULL DEFAULT '0',
  `end_type` tinyint(1) NOT NULL DEFAULT '0',
  `overlay` tinyint(1) NOT NULL DEFAULT '0',
  `artist` varchar(255) NOT NULL,
  `original_artist` varchar(255) NOT NULL,
  `title` varchar(255) NOT NULL,
  `album` varchar(255) NOT NULL,
  `composer` varchar(255) NOT NULL,
  `year` varchar(4) NOT NULL DEFAULT '1900',
  `track_no` smallint(6) NOT NULL DEFAULT '0',
  `disc_no` smallint(6) NOT NULL DEFAULT '0',
  `publisher` varchar(255) NOT NULL,
  `copyright` varchar(255) NOT NULL,
  `isrc` varchar(255) NOT NULL,
  `bpm` double(11,1) NOT NULL,
  `comments` text,
  `sweepers` varchar(250) DEFAULT NULL,
  `album_art` varchar(255) NOT NULL DEFAULT 'no_image.jpg',
  `buy_link` varchar(255) NOT NULL DEFAULT 'http://',
  `tdate_played` datetime DEFAULT '2002-01-01 00:00:01',
  `tartist_played` datetime DEFAULT '2002-01-01 00:00:01',
  PRIMARY KEY (`ID`),
  UNIQUE KEY `path` (`path`),
  KEY `title` (`title`),
  KEY `artist` (`artist`),
  KEY `date_played` (`date_played`),
  KEY `artist_played` (`artist_played`),
  KEY `count_played` (`count_played`),
  KEY `id_subcat` (`id_subcat`)
) ENGINE=MyISAM AUTO_INCREMENT=25765 DEFAULT CHARSET=utf8;

CREATE TABLE `subcategory` (
  `ID` int(11) NOT NULL AUTO_INCREMENT,
  `parentid` int(11) NOT NULL,
  `name` varchar(100) NOT NULL,
  PRIMARY KEY (`ID`)
) ENGINE=MyISAM AUTO_INCREMENT=91 DEFAULT CHARSET=utf8;
