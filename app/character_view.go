package app

import (
	"novel/internal/character"
)

// GetCharacters 返回指定小说的全部角色，供前端侧边栏列表和关系图节点渲染。
func (a *App) GetCharacters(novelID int64) ([]character.Character, error) {
	return a.character.ListAllByNovel(a.ctx, novelID)
}

// GetCharacterRelations 返回指定小说的全部当前角色关系（有向边），供前端关系图渲染。
func (a *App) GetCharacterRelations(novelID int64) ([]character.CharacterRelation, error) {
	return a.character.ListCurrentByNovel(a.ctx, novelID)
}
