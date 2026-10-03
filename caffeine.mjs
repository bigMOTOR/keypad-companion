export const defaultCaffeineMinutes=120;
export function validateCaffeineMinutes(value){
  if(!Number.isInteger(value)||value<1||value>1440)throw new Error('Тривалість: від 1 хвилини до 24 годин.');
  return value;
}
