import { describe, it, expect } from 'vitest';
import { sortCardsByType } from '../../PoPunkouterSoftware/wwwroot/js/features/app-sorter.js';

function makeCard(name, status) {
  return {
    dataset: {
      name,
      status,
    },
  };
}

describe('sortCardsByType', () => {
  it('sorts alphabetically by name', () => {
    const cards = [
      makeCard('Zulu', 'disabled'),
      makeCard('Alpha', 'active'),
      makeCard('Bravo', 'broken'),
    ];

    const sorted = sortCardsByType(cards, 'alphabetical');
    expect(sorted.map((card) => card.dataset.name)).toEqual(['Alpha', 'Bravo', 'Zulu']);
  });

  it('sorts by status order, then by name', () => {
    const cards = [
      makeCard('Gamma', 'disabled'),
      makeCard('Delta', 'active'),
      makeCard('Alpha', 'active'),
      makeCard('Beta', 'broken'),
    ];

    const sorted = sortCardsByType(cards, 'status');
    expect(sorted.map((card) => `${card.dataset.status}:${card.dataset.name}`)).toEqual([
      'active:Alpha',
      'active:Delta',
      'disabled:Gamma',
      'broken:Beta',
    ]);
  });

  it('falls back to alphabetical sorting for unknown sort type', () => {
    const cards = [
      makeCard('Charlie', 'disabled'),
      makeCard('Alpha', 'active'),
    ];

    const sorted = sortCardsByType(cards, 'unknown');
    expect(sorted.map((card) => card.dataset.name)).toEqual(['Alpha', 'Charlie']);
  });
});
