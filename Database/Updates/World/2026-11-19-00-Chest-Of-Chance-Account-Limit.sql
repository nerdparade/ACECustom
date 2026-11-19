/* Chests of Chance: one open per ACCOUNT per timer window, instead of one per character.
 *
 * Uses the InqAccountQuest emote (EmoteType 155, docs/ACCOUNT_LOCKED_CONTENT_GUIDE.md), which ships in its own PR. Merge
 * and deploy that one FIRST: a server without type 155 skips the step, and the chests would do nothing when used.
 * Restart after this is applied: chests already in the world keep the emotes they spawned with.
 *
 * The five chest weenies are live ILT data (not in this repo's base). Each one runs Use -> InqQuest <quest> ->
 * QuestSuccess ("You have played your odds too recently, my friend.") or QuestFailure (a weighted reward roll plus
 * StampQuest <quest>). This patch swaps that single InqQuest step for InqAccountQuest, so every character on the account
 * shares the quest's existing 20-hour timer (min_Delta 72000). StampQuest and the reward rolls are untouched.
 *   98760078   Chest of Chance           2500QBChestofChance
 *   867530147  Chest of Chance           HighQBChestofChance (its InqQuest text "HighQBChestofChance@2": the @2 is a comment)
 *   98760064   Greater Chest of Chance   HighQBChestofChance7500
 *   98760073   Ultimate Chest of Chance  HighQBChestofChance10000
 *   98769001   Ultimate Chest of Chance  HighQBChestofChance10000 (second copy)
 *
 * Also fixes 98769001: its Yellow Aetheria Powder roll stamped HighQBChestofChance7500 instead of ...10000, which left the
 * 10000 chest open again after that roll (98760073 is already right).
 *
 * Checked against the 2026-11-09 ILT dump: each chest has exactly one InqQuest step, in its Use emote (5 rows), no other
 * weenie uses these quest names, and the 98769001 fix matches 1 row. Undo the first UPDATE with type 155 -> 21.
 */
START TRANSACTION;

UPDATE `weenie_properties_emote_action` a
JOIN `weenie_properties_emote` e ON e.`id` = a.`emote_Id`
SET a.`type` = 155 /* InqAccountQuest */
WHERE e.`object_Id` IN (98760078, 867530147, 98760064, 98760073, 98769001)
  AND e.`category` = 7 /* Use */
  AND a.`type` = 21 /* InqQuest */;

UPDATE `weenie_properties_emote_action` a
JOIN `weenie_properties_emote` e ON e.`id` = a.`emote_Id`
SET a.`message` = 'HighQBChestofChance10000'
WHERE e.`object_Id` = 98769001
  AND a.`type` = 22 /* StampQuest */
  AND a.`message` = 'HighQBChestofChance7500';

COMMIT;
